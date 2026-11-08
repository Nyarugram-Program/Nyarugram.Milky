using Npgsql;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Storage;

public sealed record AccountRecord(
    Guid AccountId,
    string ServerDomain,
    string AccountUuid,
    long Uin,
    string Username,
    string DisplayName);

public sealed record SessionRecord(Guid SessionId, Guid AccountId, long Generation, string Token, DateTimeOffset ExpiresAt);

public sealed record SyncCursor(long Pts, int Qts, int Seq, long DateMs, bool Initialized);

/// <summary>Account identity, encrypted sessions and the synchronisation watermark.</summary>
public sealed class AccountStore
{
    private readonly MilkyDatabase _db;
    private readonly SecretProtector _protector;

    public AccountStore(MilkyDatabase db, SecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    private const string S = MilkyDatabase.SchemaName;

    public async Task<AccountRecord> UpsertAccountAsync(
        string serverDomain, string accountUuid, long uin, string username, string displayName, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.account (server_domain, account_uuid, uin, username, display_name)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (server_domain, account_uuid) DO UPDATE
                SET uin = EXCLUDED.uin, username = EXCLUDED.username,
                    display_name = EXCLUDED.display_name, updated_at = now()
            RETURNING account_id, server_domain, account_uuid, uin, username, display_name
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = serverDomain });
        command.Parameters.Add(new NpgsqlParameter { Value = accountUuid });
        command.Parameters.Add(new NpgsqlParameter { Value = uin });
        command.Parameters.Add(new NpgsqlParameter { Value = username });
        command.Parameters.Add(new NpgsqlParameter { Value = displayName });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadAccount(reader);
    }

    public async Task<AccountRecord?> GetAccountAsync(string serverDomain, string accountUuid, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT account_id, server_domain, account_uuid, uin, username, display_name
            FROM {S}.account WHERE server_domain = $1 AND account_uuid = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = serverDomain });
        command.Parameters.Add(new NpgsqlParameter { Value = accountUuid });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    /// <summary>First-phase deployments bind exactly one account per instance.</summary>
    public async Task<AccountRecord?> GetSingleAccountAsync(CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT account_id, server_domain, account_uuid, uin, username, display_name
            FROM {S}.account ORDER BY created_at LIMIT 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    /// <summary>
    /// Resolves the account bound to a configured server identity. A stored account that
    /// belongs to another server must never be restored into this deployment.
    /// </summary>
    public async Task<AccountRecord?> GetAccountByDomainAsync(string serverDomain, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT account_id, server_domain, account_uuid, uin, username, display_name
            FROM {S}.account WHERE server_domain = $1 ORDER BY created_at LIMIT 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = serverDomain });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAccount(reader) : null;
    }

    public async Task<IReadOnlyList<AccountRecord>> ListAccountsAsync(string serverDomain, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT account_id, server_domain, account_uuid, uin, username, display_name
            FROM {S}.account WHERE server_domain = $1 ORDER BY created_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = serverDomain });
        var rows = new List<AccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadAccount(reader));
        return rows;
    }

    public async Task<long> NextGenerationAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(MAX(generation), 0) + 1 FROM {S}.session WHERE account_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long value ? value : 1;
    }

    /// <summary>Revokes prior sessions and stores the new bearer token encrypted.</summary>
    public async Task<SessionRecord> ReplaceSessionAsync(
        Guid accountId, string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var (cipher, nonce, tag) = _protector.Protect(token);
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var revoke = new NpgsqlCommand(
            $"UPDATE {S}.session SET revoked_at = now() WHERE account_id = $1 AND revoked_at IS NULL", connection, transaction))
        {
            revoke.Parameters.Add(new NpgsqlParameter { Value = accountId });
            await revoke.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.session (account_id, generation, token_cipher, token_nonce, token_tag, expires_at)
            SELECT $1, COALESCE(MAX(generation), 0) + 1, $2, $3, $4, $5
            FROM {S}.session WHERE account_id = $1
            RETURNING session_id, account_id, generation, expires_at
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = cipher });
        command.Parameters.Add(new NpgsqlParameter { Value = nonce });
        command.Parameters.Add(new NpgsqlParameter { Value = tag });
        command.Parameters.Add(new NpgsqlParameter { Value = expiresAt });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var session = new SessionRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), token, reader.GetFieldValue<DateTimeOffset>(3));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return session;
    }

    public async Task<SessionRecord?> GetActiveSessionAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, account_id, generation, token_cipher, token_nonce, token_tag, expires_at
            FROM {S}.session WHERE account_id = $1 AND revoked_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var token = _protector.Unprotect(
            reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5));
        return new SessionRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), token, reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task RevokeSessionsAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"UPDATE {S}.session SET revoked_at = now() WHERE account_id = $1 AND revoked_at IS NULL", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<SyncCursor> GetCursorAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT pts, qts, seq, date_ms, initialized FROM {S}.sync_state WHERE account_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new SyncCursor(0, 0, 0, 0, false);
        return new SyncCursor(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetBoolean(4));
    }

    /// <summary>
    /// Advances the cursor. Callers must commit message/outbox rows first and pass the
    /// resulting pts; a lower pts than the stored value is rejected by the database.
    /// </summary>
    public async Task SaveCursorAsync(Guid accountId, SyncCursor cursor, CancellationToken ct, NpgsqlTransaction? transaction = null)
    {
        var connection = transaction?.Connection ?? await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {S}.sync_state (account_id, pts, qts, seq, date_ms, initialized, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, now())
                ON CONFLICT (account_id) DO UPDATE
                    SET pts = EXCLUDED.pts, qts = EXCLUDED.qts, seq = EXCLUDED.seq,
                        date_ms = EXCLUDED.date_ms, initialized = EXCLUDED.initialized, updated_at = now()
                    WHERE {S}.sync_state.pts <= EXCLUDED.pts
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = accountId });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.Pts });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.Qts });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.Seq });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.DateMs });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.Initialized });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static AccountRecord ReadAccount(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5));
}
