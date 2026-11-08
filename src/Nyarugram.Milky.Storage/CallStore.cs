using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Nyarugram.Milky.Storage;

public static class CallStates
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string Ringing = "ringing";
    public const string Accepted = "accepted";
    public const string Negotiating = "negotiating";
    public const string Active = "active";
    public const string Ended = "ended";
    public const string Failed = "failed";

    /// <summary>Terminal from the application's point of view; reconciliation may still run.</summary>
    public const string Ending = "ending";

    public static bool IsTerminal(string state) => state is Ended or Failed;
    public static bool IsOpen(string state) => state is not (Ended or Failed);
}

public static class MediaStages
{
    public const string None = "none";
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Closed = "closed";
}

public static class KeyStates
{
    public const string None = "none";
    public const string Unverified = "unverified";
    public const string Verified = "verified";
    public const string Failed = "failed";
}

public static class AcceptStages
{
    public const string Prepared = "prepared";
    public const string Submitted = "submitted";
    public const string Accepted = "accepted";
}

public sealed record CallRecord(
    Guid CallId,
    Guid AccountId,
    long Generation,
    string? InstanceId,
    long? BackendCallId,
    long? AccessHash,
    string Direction,
    long PeerUin,
    string State,
    long StateVersion,
    long MediaVersion,
    bool Video,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    string MediaState,
    string? MediaReason,
    string? ReconcileState,
    int ReconcileAttempts,
    byte[]? PeerGaHash,
    string KeyState,
    string MediaStage,
    int MediaAttempts,
    DateTimeOffset? MediaNextAttemptAt,
    string? MediaLastError,
    string? PendingEndReason,
    string? AcceptStage)
{
    public bool IsTerminal => CallStates.IsTerminal(State);
}

public sealed record CallSecret(byte[] Cipher, byte[] Nonce, byte[] Tag);

public sealed record AcceptMaterial(CallSecret Secret, byte[] PublicValue, string Stage, bool Claimed);

public sealed record CallOperation(
    string IdempotencyKey,
    string RequestHash,
    long RandomId,
    byte[] GaHash,
    CallSecret Secret,
    string Stage,
    Guid? CallId,
    DateTimeOffset? LeaseUntil,
    Guid? LeaseToken,
    int Attempts,
    string? LastError);

/// <summary>
/// Adapter-side call projection plus the persisted start/accept operations.
///
/// State, media and hangup transitions that must be observable write their outbox row in
/// the same transaction, so a committed state can never be missing its event.
/// </summary>
public sealed class CallStore
{
    private readonly MilkyDatabase _db;

    public CallStore(MilkyDatabase db) => _db = db;

    private const string S = MilkyDatabase.SchemaName;

    private const string Columns =
        "call_id, account_id, generation, instance_id, backend_call_id, access_hash, direction, peer_uin, state, " +
        "state_version, media_version, video, created_at, connected_at, ended_at, end_reason, media_state, " +
        "media_reason, reconcile_state, reconcile_attempts, peer_ga_hash, key_state, media_stage, media_attempts, " +
        "media_next_attempt_at, media_last_error, pending_end_reason, accept_stage";

    public async Task<CallRecord> CreateAsync(
        Guid accountId, long generation, string? instanceId, string direction, long peerUin, bool video,
        string state, CancellationToken ct, byte[]? peerGaHash = null)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.call (account_id, generation, instance_id, direction, peer_uin, video, state,
                                  connected_at, peer_ga_hash)
            VALUES ($1, $2, $3, $4, $5, $6, $7, CASE WHEN $7 = 'active' THEN now() ELSE NULL END, $8)
            RETURNING {Columns}
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = generation });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)instanceId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = direction });
        command.Parameters.Add(new NpgsqlParameter { Value = peerUin });
        command.Parameters.Add(new NpgsqlParameter { Value = video });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)peerGaHash ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Read(reader);
    }

    /// <summary>Creates the projection and its first event in one transaction.</summary>
    public async Task<CallRecord> CreateWithEventAsync(
        Guid accountId, long generation, string? instanceId, string direction, long peerUin, bool video,
        string state, Func<CallRecord, (string EventId, string EventType, string Payload)> eventFactory,
        CancellationToken ct, byte[]? peerGaHash = null)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        CallRecord created;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {S}.call (account_id, generation, instance_id, direction, peer_uin, video, state,
                                  connected_at, peer_ga_hash)
            VALUES ($1, $2, $3, $4, $5, $6, $7, CASE WHEN $7 = 'active' THEN now() ELSE NULL END, $8)
            RETURNING {Columns}
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = accountId });
            command.Parameters.Add(new NpgsqlParameter { Value = generation });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)instanceId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = direction });
            command.Parameters.Add(new NpgsqlParameter { Value = peerUin });
            command.Parameters.Add(new NpgsqlParameter { Value = video });
            command.Parameters.Add(new NpgsqlParameter { Value = state });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)peerGaHash ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            created = Read(reader);
        }

        var (eventId, eventType, payload) = eventFactory(created);
        await OutboxStore.EnqueueAsync(connection, transaction, accountId, eventId, eventType, payload, ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return created;
    }

    public async Task<CallRecord?> GetAsync(Guid accountId, Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {S}.call WHERE account_id = $1 AND call_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<CallRecord?> GetByBackendAsync(Guid accountId, long backendCallId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {S}.call WHERE account_id = $1 AND backend_call_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = backendCallId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<CallRecord> AttachBackendAsync(
        Guid callId, long backendCallId, long accessHash, string instanceId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET backend_call_id = $2, access_hash = $3, instance_id = $4, updated_at = now()
            WHERE call_id = $1
            RETURNING {Columns}
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = backendCallId });
        command.Parameters.Add(new NpgsqlParameter { Value = accessHash });
        command.Parameters.Add(new NpgsqlParameter { Value = instanceId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Read(reader);
    }

    /// <summary>
    /// Compare-and-set state transition. Returns null when another writer already advanced
    /// the version, so late callbacks cannot resurrect or close an unrelated call.
    /// </summary>
    public async Task<CallRecord?> TransitionAsync(
        Guid callId, long expectedVersion, string state, string? endReason,
        string? mediaState, string? mediaReason, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET state = $3,
                state_version = state_version + 1,
                connected_at = CASE WHEN $3 = 'active' AND connected_at IS NULL THEN now() ELSE connected_at END,
                ended_at = CASE WHEN $3 IN ('ended','failed') THEN COALESCE(ended_at, now()) ELSE ended_at END,
                end_reason = CASE WHEN $3 IN ('ended','failed') THEN COALESCE($4, end_reason) ELSE end_reason END,
                media_state = COALESCE($5, media_state),
                media_reason = CASE WHEN $3 IN ('ended','failed') THEN COALESCE($6, media_reason) ELSE media_reason END,
                pending_end_reason = CASE WHEN $3 IN ('ended','failed') THEN NULL ELSE pending_end_reason END,
                pending_end_at = CASE WHEN $3 IN ('ended','failed') THEN NULL ELSE pending_end_at END,
                updated_at = now()
            WHERE call_id = $1 AND state_version = $2 AND ended_at IS NULL
            RETURNING {Columns}
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)endReason ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaState ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaReason ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>
    /// State transition and its event commit together. A failed insert rolls the state back,
    /// so an observable state can never exist without its event. A terminal transition also
    /// closes media atomically: the media state flips to closed and, when the call was carrying
    /// media, a media-closed event is written in the same transaction.
    /// </summary>
    public async Task<(CallRecord? Record, bool Applied)> TransitionWithEventAsync(
        Guid accountId, Guid callId, long expectedVersion, string state, string? endReason,
        string? mediaState, string? mediaReason,
        Func<CallRecord, (string EventId, string EventType, string Payload)> eventFactory,
        CancellationToken ct,
        Func<CallRecord, (string EventId, string EventType, string Payload)>? mediaEventFactory = null)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var terminal = state is CallStates.Ended or CallStates.Failed;

        CallRecord? updated;
        await using (var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET state = $3,
                state_version = state_version + 1,
                connected_at = CASE WHEN $3 = 'active' AND connected_at IS NULL THEN now() ELSE connected_at END,
                ended_at = CASE WHEN $3 IN ('ended','failed') THEN COALESCE(ended_at, now()) ELSE ended_at END,
                end_reason = CASE WHEN $3 IN ('ended','failed') THEN COALESCE($4, end_reason) ELSE end_reason END,
                media_state = CASE WHEN $3 IN ('ended','failed') THEN 'closed' ELSE COALESCE($5, media_state) END,
                media_reason = CASE WHEN $3 IN ('ended','failed') THEN COALESCE($6, media_reason) ELSE media_reason END,
                pending_end_reason = CASE WHEN $3 IN ('ended','failed') THEN NULL ELSE pending_end_reason END,
                pending_end_at = CASE WHEN $3 IN ('ended','failed') THEN NULL ELSE pending_end_at END,
                media_stage = CASE WHEN $3 IN ('ended','failed') THEN 'closed' ELSE media_stage END,
                media_version = CASE WHEN $3 IN ('ended','failed') THEN media_version + 1 ELSE media_version END,
                updated_at = now()
            WHERE call_id = $1 AND state_version = $2 AND ended_at IS NULL
            RETURNING {Columns}
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = callId });
            command.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = state });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)endReason ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaState ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaReason ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            updated = await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
        }

        if (updated is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (null, false);
        }

        var (eventId, eventType, payload) = eventFactory(updated);
        await OutboxStore.EnqueueAsync(connection, transaction, accountId, eventId, eventType, payload, ct)
            .ConfigureAwait(false);

        if (terminal && mediaEventFactory is not null)
        {
            var (mediaEventId, mediaEventType, mediaPayload) = mediaEventFactory(updated);
            await OutboxStore.EnqueueAsync(connection, transaction, accountId, mediaEventId, mediaEventType, mediaPayload, ct)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (updated, true);
    }

    /// <summary>
    /// Advances the application-facing media state and its internal stage with a new media
    /// version, writing the optional event in the same transaction.
    /// </summary>
    public async Task<(CallRecord? Record, bool Applied)> SetMediaAsync(
        Guid accountId, Guid callId, string mediaState, string mediaStage, string? mediaReason,
        Func<CallRecord, (string EventId, string EventType, string Payload)>? eventFactory, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        CallRecord? updated;
        await using (var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET media_state = $2, media_stage = $3, media_reason = $4,
                media_version = media_version + 1,
                media_next_attempt_at = NULL,
                media_last_error = CASE WHEN $3 = 'failed' THEN COALESCE($4, media_last_error) ELSE media_last_error END,
                updated_at = now()
            WHERE call_id = $1 AND ended_at IS NULL
            RETURNING {Columns}
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = callId });
            command.Parameters.Add(new NpgsqlParameter { Value = mediaState });
            command.Parameters.Add(new NpgsqlParameter { Value = mediaStage });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaReason ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            updated = await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
        }

        if (updated is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (null, false);
        }

        await using (var mark = new NpgsqlCommand($"""
            INSERT INTO {S}.call_media_event (account_id, call_id, media_version, media_state)
            VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            mark.Parameters.Add(new NpgsqlParameter { Value = accountId });
            mark.Parameters.Add(new NpgsqlParameter { Value = callId });
            mark.Parameters.Add(new NpgsqlParameter { Value = updated.MediaVersion });
            mark.Parameters.Add(new NpgsqlParameter { Value = mediaState });
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (eventFactory is not null)
        {
            var (eventId, eventType, payload) = eventFactory(updated);
            await OutboxStore.EnqueueAsync(connection, transaction, accountId, eventId, eventType, payload, ct)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return (updated, true);
    }

    /// <summary>Schedules a bounded media retry without touching the signalling state.</summary>
    public async Task ScheduleMediaRetryAsync(
        Guid callId, string error, DateTimeOffset nextAttempt, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET media_stage = 'pending', media_state = 'pending', media_attempts = media_attempts + 1,
                media_next_attempt_at = $2, media_last_error = $3, updated_at = now()
            WHERE call_id = $1 AND ended_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = nextAttempt });
        command.Parameters.Add(new NpgsqlParameter { Value = error });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CallRecord>> ListMediaRecoveryAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {S}.call
            WHERE account_id = $1 AND ended_at IS NULL AND media_stage = 'pending'
              AND (media_next_attempt_at IS NULL OR media_next_attempt_at <= now())
            ORDER BY created_at
            LIMIT 20
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var rows = new List<CallRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Persists the latest backend PhoneCall snapshot for later recovery.</summary>
    public async Task SaveSnapshotAsync(Guid callId, byte[]? blob, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"UPDATE {S}.call SET last_call_blob = $2, updated_at = now() WHERE call_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)blob ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetSnapshotAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT last_call_blob FROM {S}.call WHERE call_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as byte[];
    }

    public async Task MarkKeyVerifiedAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call SET key_state = 'verified', key_verified_at = now(), updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkKeyFailedAsync(Guid callId, string reason, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call SET key_state = 'failed', media_last_error = $2, updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------- accept material

    /// <summary>
    /// Atomically claims the accept slot. If another attempt already stored material, the
    /// stored g_b and secret are returned instead of generating a second key.
    /// </summary>
    public async Task<AcceptMaterial> ClaimAcceptAsync(
        Guid callId, Guid accountId, Func<(CallSecret Secret, byte[] PublicValue)> create, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var (secret, publicValue) = create();
        await using (var claim = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET accept_secret_cipher = $3, accept_secret_nonce = $4, accept_secret_tag = $5,
                accept_gb = $6, accept_stage = 'prepared', updated_at = now()
            WHERE call_id = $1 AND account_id = $2 AND accept_stage IS NULL AND ended_at IS NULL
            """, connection, transaction))
        {
            claim.Parameters.Add(new NpgsqlParameter { Value = callId });
            claim.Parameters.Add(new NpgsqlParameter { Value = accountId });
            claim.Parameters.Add(new NpgsqlParameter { Value = secret.Cipher });
            claim.Parameters.Add(new NpgsqlParameter { Value = secret.Nonce });
            claim.Parameters.Add(new NpgsqlParameter { Value = secret.Tag });
            claim.Parameters.Add(new NpgsqlParameter { Value = publicValue });
            var claimed = await claim.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (claimed)
                return new AcceptMaterial(secret, publicValue, AcceptStages.Prepared, true);
        }

        await using var read = new NpgsqlCommand($"""
            SELECT accept_secret_cipher, accept_secret_nonce, accept_secret_tag, accept_gb, accept_stage
            FROM {S}.call WHERE call_id = $1 AND account_id = $2
            """, connection);
        read.Parameters.Add(new NpgsqlParameter { Value = callId });
        read.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
            throw new InvalidOperationException("accept material vanished after claim");
        return new AcceptMaterial(
            new CallSecret(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2)),
            reader.GetFieldValue<byte[]>(3), reader.GetString(4), false);
    }

    public async Task SetAcceptStageAsync(Guid callId, string stage, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call SET accept_stage = $2, updated_at = now()
            WHERE call_id = $1 AND (accept_stage IS NULL OR accept_stage <> 'accepted')
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = stage });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<CallSecret?> GetAcceptSecretAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT accept_secret_cipher, accept_secret_nonce, accept_secret_tag
            FROM {S}.call WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0)) return null;
        return new CallSecret(
            reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2));
    }

    // ------------------------------------------------------- pending hangup

    public async Task SetPendingEndAsync(Guid callId, string reason, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET pending_end_reason = COALESCE(pending_end_reason, $2),
                pending_end_at = COALESCE(pending_end_at, now()),
                reconcile_state = 'pending',
                reconcile_next_at = now(),
                updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CallRecord>> ListPendingEndAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {S}.call
            WHERE account_id = $1 AND pending_end_reason IS NOT NULL
              AND (reconcile_next_at IS NULL OR reconcile_next_at <= now())
            ORDER BY pending_end_at
            LIMIT 20
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var rows = new List<CallRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// Clears a pending-release marker once the backend confirmed the release. Distinct from a
    /// state transition: a call that already failed locally keeps its terminal state and end
    /// reason, only the "line still busy on the backend" marker is dropped.
    /// </summary>
    public async Task ClearPendingEndAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET pending_end_reason = NULL, pending_end_at = NULL,
                reconcile_state = NULL, reconcile_next_at = NULL, updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CallRecord>> ListOpenAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {S}.call
            WHERE account_id = $1 AND ended_at IS NULL
            ORDER BY created_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var rows = new List<CallRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Open calls that were created by a different process instance.</summary>
    public async Task<IReadOnlyList<CallRecord>> ListForeignInstanceAsync(
        Guid accountId, string instanceId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {S}.call
            WHERE account_id = $1 AND ended_at IS NULL
              AND (instance_id IS NULL OR instance_id <> $2)
            ORDER BY created_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = instanceId });
        var rows = new List<CallRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// Calls whose release is still pending reconciliation, whether from a startup cleanup of a
    /// foreign instance, a hangup whose backend outcome is unknown, or a local failure that
    /// left the line busy. The maintenance worker keeps retrying these until the backend
    /// confirms the release.
    /// </summary>
    public async Task<IReadOnlyList<CallRecord>> ListPendingReconcileAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM {S}.call
            WHERE account_id = $1 AND reconcile_state = 'pending'
              AND (reconcile_next_at IS NULL OR reconcile_next_at <= now())
            ORDER BY reconcile_next_at NULLS FIRST, created_at
            LIMIT 20
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var rows = new List<CallRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    public async Task MarkReconcilingAsync(
        Guid callId, string? error, TimeSpan nextAttempt, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call
            SET reconcile_state = 'pending', reconcile_attempts = reconcile_attempts + 1,
                reconcile_next_at = now() + $3::interval, last_error = $2, updated_at = now()
            WHERE call_id = $1 AND ended_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)error ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = nextAttempt });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearReconcilingAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call SET reconcile_state = NULL, last_error = NULL, updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stores the encrypted call secret (g^a for a caller, g^b for a callee).</summary>
    public async Task SaveSecretAsync(Guid callId, byte[] cipher, byte[] nonce, byte[] tag, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call SET secret_cipher = $2, secret_nonce = $3, secret_tag = $4, updated_at = now()
            WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = cipher });
        command.Parameters.Add(new NpgsqlParameter { Value = nonce });
        command.Parameters.Add(new NpgsqlParameter { Value = tag });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<CallSecret?> GetSecretAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT secret_cipher, secret_nonce, secret_tag FROM {S}.call WHERE call_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0)) return null;
        return new CallSecret(
            reader.GetFieldValue<byte[]>(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2));
    }

    // ------------------------------------------------------- media tokens

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task CreateMediaTokenAsync(
        Guid accountId, Guid callId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.call_media_token (token_hash, call_id, account_id, expires_at)
            VALUES ($1, $2, $3, $4)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tokenHash });
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = expiresAt });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a media token to its call, rejecting expired, closed or foreign calls.</summary>
    public async Task<(Guid CallId, Guid AccountId, long Generation)?> ResolveMediaTokenAsync(
        byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT t.call_id, t.account_id, c.generation
            FROM {S}.call_media_token t
            JOIN {S}.call c ON c.call_id = t.call_id
            WHERE t.token_hash = $1 AND t.expires_at > now() AND c.ended_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2));
    }

    public async Task RevokeMediaTokensAsync(Guid callId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command =
            new NpgsqlCommand($"DELETE FROM {S}.call_media_token WHERE call_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = callId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // --------------------------------------------------- start operations

    /// <summary>
    /// Claims the idempotency key. A fresh claim inserts the persisted random id, g_a hash
    /// and encrypted DH secret, so a crash after this point can replay the same call.
    /// Returns the existing operation when the same payload already owns the key.
    /// </summary>
    public async Task<(CallOperation Operation, bool Created)> ClaimOperationAsync(
        Guid accountId, string key, string requestHash, long randomId, byte[] gaHash, CallSecret secret,
        CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {S}.call_operation
                (account_id, idempotency_key, request_hash, random_id, ga_hash,
                 secret_cipher, secret_nonce, secret_tag, stage)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'prepared')
            ON CONFLICT (account_id, idempotency_key) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = accountId });
            insert.Parameters.Add(new NpgsqlParameter { Value = key });
            insert.Parameters.Add(new NpgsqlParameter { Value = requestHash });
            insert.Parameters.Add(new NpgsqlParameter { Value = randomId });
            insert.Parameters.Add(new NpgsqlParameter { Value = gaHash });
            insert.Parameters.Add(new NpgsqlParameter { Value = secret.Cipher });
            insert.Parameters.Add(new NpgsqlParameter { Value = secret.Nonce });
            insert.Parameters.Add(new NpgsqlParameter { Value = secret.Tag });
            var created = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (created)
                return (new CallOperation(key, requestHash, randomId, gaHash, secret, "prepared", null, null, null, 0, null), true);
        }

        var existing = await GetOperationAsync(accountId, key, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("call operation vanished");
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            throw new IdempotencyConflictException("call_start", key);
        return (existing, false);
    }

    public async Task<CallOperation?> GetOperationAsync(Guid accountId, string key, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT idempotency_key, request_hash, random_id, ga_hash, secret_cipher, secret_nonce, secret_tag,
                   stage, call_id, lease_until, lease_token, attempts, last_error
            FROM {S}.call_operation WHERE account_id = $1 AND idempotency_key = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new CallOperation(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3),
            new CallSecret(reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6)),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    /// <summary>
    /// Acquires a lease owned by a unique token. A terminal operation cannot be leased, so a
    /// late retry never reopens a completed start.
    /// </summary>
    public async Task<Guid?> TryLeaseOperationAsync(
        Guid accountId, string key, TimeSpan lease, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call_operation
            SET lease_until = now() + $3::interval, lease_token = $4,
                attempts = attempts + 1, updated_at = now()
            WHERE account_id = $1 AND idempotency_key = $2
              AND stage <> 'failed'
              AND (lease_until IS NULL OR lease_until < now())
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? token : null;
    }

    /// <summary>Renews only when this caller still owns the lease.</summary>
    public async Task<bool> RenewOperationLeaseAsync(
        Guid accountId, string key, Guid token, TimeSpan lease, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call_operation
            SET lease_until = now() + $4::interval, updated_at = now()
            WHERE account_id = $1 AND idempotency_key = $2 AND lease_token = $3
              AND lease_until IS NOT NULL AND lease_until > now()
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        command.Parameters.Add(new NpgsqlParameter { Value = lease });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task ReleaseOperationLeaseAsync(Guid accountId, string key, Guid token, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call_operation SET lease_until = NULL, lease_token = NULL, updated_at = now()
            WHERE account_id = $1 AND idempotency_key = $2 AND lease_token = $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stage writes are rejected when the caller no longer owns the lease.</summary>
    public async Task<bool> UpdateOperationStageAsync(
        Guid accountId, string key, Guid token, string stage, Guid? callId, string? error, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.call_operation
            SET stage = $4, call_id = COALESCE($5, call_id), last_error = $6, updated_at = now()
            WHERE account_id = $1 AND idempotency_key = $2 AND lease_token = $3
              AND stage <> 'failed'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        command.Parameters.Add(new NpgsqlParameter { Value = stage });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)callId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)error ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static CallRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetInt64(9), reader.GetInt64(10),
        reader.GetBoolean(11),
        reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.GetString(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.GetInt32(19),
        reader.IsDBNull(20) ? null : reader.GetFieldValue<byte[]>(20),
        reader.GetString(21),
        reader.GetString(22),
        reader.GetInt32(23),
        reader.IsDBNull(24) ? null : reader.GetFieldValue<DateTimeOffset>(24),
        reader.IsDBNull(25) ? null : reader.GetString(25),
        reader.IsDBNull(26) ? null : reader.GetString(26),
        reader.IsDBNull(27) ? null : reader.GetString(27));
}
