using Npgsql;

namespace Nyarugram.Milky.Storage;

public enum PeerKind
{
    User,
    Group,
}

public sealed record MessageRow(
    long MessageId,
    PeerKind PeerKind,
    long PeerUin,
    long SenderUin,
    long CreatedAtMs,
    bool Outgoing);

public sealed record SkippedMessage(long MessageId, string Reason, DateTimeOffset CreatedAt);

/// <summary>
/// Account-scoped numeric identity mapping. Nyarugram's stable TL id is the source of
/// the Milky number; this table records and verifies the binding so a changed
/// username, nickname or Gram ID can never silently re-point an identity.
/// </summary>
public sealed class IdentityStore
{
    private readonly MilkyDatabase _db;

    public IdentityStore(MilkyDatabase db) => _db = db;

    private const string S = MilkyDatabase.SchemaName;

    public async Task<long?> FindUinAsync(Guid accountId, PeerKind kind, string externalId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT uin FROM {S}.id_map WHERE account_id = $1 AND kind = $2 AND external_id = $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = Kind(kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = externalId });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long uin ? uin : null;
    }

    public async Task<string?> FindExternalIdAsync(Guid accountId, PeerKind kind, long uin, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT external_id FROM {S}.id_map WHERE account_id = $1 AND kind = $2 AND uin = $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = Kind(kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = uin });
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Records the binding. A different external id or uin for an existing pair throws,
    /// because re-pointing an identity would corrupt every stored message reference.
    /// </summary>
    public async Task RememberAsync(Guid accountId, PeerKind kind, string externalId, long uin, CancellationToken ct)
    {
        if (uin <= 0) throw new ArgumentOutOfRangeException(nameof(uin), "numeric identity must be positive");
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.id_map (account_id, kind, external_id, uin)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (account_id, kind, external_id) DO UPDATE
                SET updated_at = now()
                WHERE {S}.id_map.uin = EXCLUDED.uin
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = Kind(kind) });
        command.Parameters.Add(new NpgsqlParameter { Value = externalId });
        command.Parameters.Add(new NpgsqlParameter { Value = uin });
        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
            throw new InvalidOperationException(
                $"identity {Kind(kind)}:{externalId} already mapped to a different numeric id");
    }

    public async Task UpsertMessagesAsync(Guid accountId, IReadOnlyList<MessageRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.message (account_id, message_id, peer_kind, peer_uin, sender_uin, created_at_ms, outgoing)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (account_id, message_id) DO NOTHING
            """, connection, transaction);
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            command.Parameters.Add(new NpgsqlParameter { Value = accountId });
            command.Parameters.Add(new NpgsqlParameter { Value = row.MessageId });
            command.Parameters.Add(new NpgsqlParameter { Value = row.PeerKind == PeerKind.Group ? "group" : "friend" });
            command.Parameters.Add(new NpgsqlParameter { Value = row.PeerUin });
            command.Parameters.Add(new NpgsqlParameter { Value = row.SenderUin });
            command.Parameters.Add(new NpgsqlParameter { Value = row.CreatedAtMs });
            command.Parameters.Add(new NpgsqlParameter { Value = row.Outgoing });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<MessageRow?> GetMessageAsync(Guid accountId, long messageId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT message_id, peer_kind, peer_uin, sender_uin, created_at_ms, outgoing
            FROM {S}.message WHERE account_id = $1 AND message_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new MessageRow(
            reader.GetInt64(0),
            reader.GetString(1) == "group" ? PeerKind.Group : PeerKind.User,
            reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetBoolean(5));
    }

    /// <summary>
    /// Records a message that cannot be represented. Skips are auditable instead of being
    /// silently swallowed while the sync cursor moves on.
    /// </summary>
    public static async Task RecordSkipAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        long messageId, string reason, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.message_skip (account_id, message_id, reason)
            VALUES ($1, $2, $3) ON CONFLICT (account_id, message_id) DO NOTHING
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkippedMessage>> ListSkipsAsync(
        Guid accountId, int limit, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT message_id, reason, created_at FROM {S}.message_skip
            WHERE account_id = $1 ORDER BY created_at DESC LIMIT $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var rows = new List<SkippedMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new SkippedMessage(reader.GetInt64(0), reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        return rows;
    }

    private static string Kind(PeerKind kind) => kind == PeerKind.Group ? "group" : "user";
}

public sealed record IdempotencyRecord(
    string RequestHash,
    long? RandomId,
    string? ResultJson,
    string State,
    int Attempts,
    string? LastError,
    string? RequestJson = null)
{
    public bool Completed => State == "completed";
    public bool OutcomeUnknown => State == "unknown";
}

public static class IdempotencyStates
{
    public const string Pending = "pending";
    public const string Submitted = "submitted";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>
/// Persisted send operations. The random id and the frozen request identity are written
/// before the backend is contacted, so a retry after a timeout, a dropped response or a
/// process restart reuses the same logical operation instead of creating a second message.
/// </summary>
public sealed class IdempotencyStore
{
    private readonly MilkyDatabase _db;

    public IdempotencyStore(MilkyDatabase db) => _db = db;

    private const string S = MilkyDatabase.SchemaName;

    public async Task<IdempotencyRecord?> GetAsync(Guid accountId, string scope, string key, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await GetAsync(connection, null, accountId, scope, key, ct).ConfigureAwait(false);
    }

    private static async Task<IdempotencyRecord?> GetAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid accountId, string scope, string key,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_hash, random_id, result_json, state, attempts, last_error, request_json::text
            FROM {S}.idempotency WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new IdempotencyRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>
    /// Claims the key with the full request identity. Returns the existing record when the
    /// same payload already owns the key, and throws when the payload differs.
    /// </summary>
    public async Task<(IdempotencyRecord Record, bool Created)> ClaimAsync(
        Guid accountId, string scope, string key, string requestHash, long? randomId, string? requestJson,
        CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {S}.idempotency (account_id, scope, idempotency_key, request_hash, random_id, state, request_json)
            VALUES ($1, $2, $3, $4, $5, 'pending', $6::jsonb)
            ON CONFLICT (account_id, scope, idempotency_key) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = accountId });
            insert.Parameters.Add(new NpgsqlParameter { Value = scope });
            insert.Parameters.Add(new NpgsqlParameter { Value = key });
            insert.Parameters.Add(new NpgsqlParameter { Value = requestHash });
            insert.Parameters.Add(new NpgsqlParameter { Value = randomId ?? (object)DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = (object?)requestJson ?? DBNull.Value });
            var created = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (created)
                return (new IdempotencyRecord(requestHash, randomId, null, IdempotencyStates.Pending, 0, null, requestJson), true);
        }

        var existing = await GetAsync(accountId, scope, key, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("idempotency row vanished");
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new IdempotencyConflictException(scope, key);
        return (existing, false);
    }

    /// <summary>
    /// State writes are conditional on the lease owner and never downgrade a completed
    /// operation, so a slow executor cannot overwrite the winner's result.
    /// </summary>
    public async Task<bool> SetStateAsync(
        Guid accountId, string scope, string key, Guid token, string state, string? error, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.idempotency
            SET state = $5, last_error = $6, attempts = attempts + 1, updated_at = now()
            WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3 AND lease_token = $4
              AND state <> 'completed' AND state <> 'failed'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)error ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteAsync(
        Guid accountId, string scope, string key, Guid token, string resultJson, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.idempotency
            SET result_json = $5, state = 'completed', completed_at = now(), last_error = NULL, updated_at = now()
            WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3 AND lease_token = $4
              AND state <> 'completed'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        command.Parameters.Add(new NpgsqlParameter { Value = resultJson });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Short lease owned by a unique token. Completed or failed operations are never leased,
    /// and only the token holder can renew, release or complete.
    /// </summary>
    public async Task<Guid?> TryLeaseAsync(
        Guid accountId, string scope, string key, TimeSpan lease, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.idempotency
            SET lease_until = now() + $4::interval, lease_token = $5,
                attempts = attempts + 1, updated_at = now()
            WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3
              AND state NOT IN ('completed', 'failed')
              AND (lease_until IS NULL OR lease_until < now())
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? token : null;
    }

    public async Task<bool> RenewLeaseAsync(
        Guid accountId, string scope, string key, Guid token, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.idempotency
            SET lease_until = now() + $5::interval, updated_at = now()
            WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3 AND lease_token = $4
              AND lease_until IS NOT NULL AND lease_until > now()
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task ReleaseLeaseAsync(Guid accountId, string scope, string key, Guid token, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.idempotency SET lease_until = NULL, lease_token = NULL, updated_at = now()
            WHERE account_id = $1 AND scope = $2 AND idempotency_key = $3 AND lease_token = $4
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Operations whose outcome is still unknown after a crash or a dropped response.</summary>
    public async Task<IReadOnlyList<(string Key, IdempotencyRecord Record)>> ListUnresolvedAsync(
        Guid accountId, string scope, int limit, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT idempotency_key, request_hash, random_id, result_json, state, attempts, last_error, request_json::text
            FROM {S}.idempotency
            WHERE account_id = $1 AND scope = $2 AND state IN ('pending','submitted','unknown')
            ORDER BY created_at LIMIT $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = scope });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var rows = new List<(string, IdempotencyRecord)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add((reader.GetString(0), new IdempotencyRecord(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7))));
        return rows;
    }
}

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException(string scope, string key)
        : base($"idempotency key {scope}:{key} was already used with a different payload")
    {
        Scope = scope;
        Key = key;
    }

    public string Scope { get; }
    public string Key { get; }
}
