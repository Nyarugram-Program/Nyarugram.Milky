using Npgsql;

namespace Nyarugram.Milky.Storage;

public sealed record OutboxRow(
    long Seq, string EventId, string EventType, string Payload, DateTimeOffset CreatedAt, string State);

public sealed record OutboxMetrics(long Pending, long Expired, long Delivered, DateTimeOffset? OldestPendingAt);

/// <summary>
/// Durable event log with per-consumer replay positions.
///
/// Delivery rules:
/// - a row is 'delivered' only when every currently connected consumer has advanced past it;
/// - with no connected consumer rows stay 'pending', they are never retired as delivered;
/// - retention moves stale rows to 'expired' so the loss is visible and countable;
/// - replay reads rows strictly after a consumer cursor, including already delivered rows,
///   until they expire.
/// </summary>
public sealed class OutboxStore
{
    private readonly MilkyDatabase _db;

    public OutboxStore(MilkyDatabase db) => _db = db;

    private const string S = MilkyDatabase.SchemaName;

    public static async Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        string eventId, string eventType, string payloadJson, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.outbox (account_id, event_id, event_type, payload, state)
            VALUES ($1, $2, $3, $4::jsonb, 'pending')
            ON CONFLICT (account_id, event_id) DO NOTHING
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter { Value = payloadJson });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(
        Guid accountId, string eventId, string eventType, string payloadJson, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EnqueueAsync(connection, transaction, accountId, eventId, eventType, payloadJson, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rows still worth sending to a consumer positioned at <paramref name="afterSeq"/>.</summary>
    public async Task<IReadOnlyList<OutboxRow>> FetchAfterAsync(
        Guid accountId, long afterSeq, int limit, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT event_seq, event_id, event_type, payload::text, created_at, state
            FROM {S}.outbox
            WHERE account_id = $1 AND event_seq > $2 AND state <> 'expired'
            ORDER BY event_seq
            LIMIT $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = afterSeq });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new OutboxRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5)));
        return rows;
    }

    /// <summary>Marks rows delivered up to a sequence that every connected consumer reached.</summary>
    public async Task<int> MarkDeliveredThroughAsync(Guid accountId, long seq, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.outbox
            SET state = 'delivered', delivered_at = now()
            WHERE account_id = $1 AND event_seq <= $2 AND state = 'pending'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = seq });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retention: pending rows older than the window become 'expired'. They are kept so the
    /// loss is observable; replay skips them and consumers are told about the gap.
    /// </summary>
    public async Task<int> ExpireAsync(Guid accountId, TimeSpan retention, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.outbox
            SET state = 'expired', expired_at = now()
            WHERE account_id = $1 AND state = 'pending' AND created_at < now() - $2::interval
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = retention });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Hard retention for rows that were delivered or expired long ago.</summary>
    public async Task<int> PurgeAsync(Guid accountId, TimeSpan retention, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            DELETE FROM {S}.outbox
            WHERE account_id = $1 AND state <> 'pending'
              AND COALESCE(expired_at, delivered_at) < now() - $2::interval
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = retention + retention });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<OutboxMetrics> GetMetricsAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT
                count(*) FILTER (WHERE state = 'pending'),
                count(*) FILTER (WHERE state = 'expired'),
                count(*) FILTER (WHERE state = 'delivered'),
                min(created_at) FILTER (WHERE state = 'pending')
            FROM {S}.outbox WHERE account_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new OutboxMetrics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    /// <summary>Latest sequence ever written, used to start a live-only consumer.</summary>
    public async Task<long> LatestSeqAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT COALESCE(max(event_seq), 0) FROM {S}.outbox WHERE account_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long seq ? seq : 0;
    }

    /// <summary>Oldest sequence still replayable; a cursor below it has lost events.</summary>
    public async Task<long> OldestReplayableSeqAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(min(event_seq), 0) FROM {S}.outbox
            WHERE account_id = $1 AND state <> 'expired'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long seq ? seq : 0;
    }

    // ------------------------------------------------------- consumer cursors

    public async Task<long> GetConsumerCursorAsync(Guid accountId, string consumerId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT last_seq FROM {S}.event_cursor WHERE account_id = $1 AND consumer_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = consumerId });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long seq ? seq : -1;
    }

    /// <summary>Cursor and its generation. A rewind bumps the generation, invalidating older writers.</summary>
    public async Task<ConsumerCursor> GetConsumerCursorEpochAsync(
        Guid accountId, string consumerId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT last_seq, epoch FROM {S}.event_cursor WHERE account_id = $1 AND consumer_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = consumerId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new ConsumerCursor(-1, 0);
        return new ConsumerCursor(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Advances the durable cursor only if the writer still holds the current generation. A
    /// stale connection whose generation was superseded by a rewind is ignored, so its late
    /// acknowledgement cannot overwrite a newer position.
    /// </summary>
    public async Task SetConsumerCursorAsync(
        Guid accountId, string consumerId, long seq, long epoch, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.event_cursor (account_id, consumer_id, last_seq, epoch)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (account_id, consumer_id) DO UPDATE
                SET last_seq = GREATEST({S}.event_cursor.last_seq, EXCLUDED.last_seq),
                    epoch = EXCLUDED.epoch,
                    updated_at = now()
                WHERE {S}.event_cursor.epoch = EXCLUDED.epoch
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = consumerId });
        command.Parameters.Add(new NpgsqlParameter { Value = seq });
        command.Parameters.Add(new NpgsqlParameter { Value = epoch });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a named consumer's starting position without clobbering an existing row. A
    /// first connection therefore leaves a durable marker even when nothing was acknowledged,
    /// so a reconnect resumes from the same position instead of being mistaken for a brand-new
    /// live-only consumer.
    /// </summary>
    public async Task EnsureConsumerAsync(Guid accountId, string consumerId, long seq, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.event_cursor (account_id, consumer_id, last_seq)
            VALUES ($1, $2, $3)
            ON CONFLICT (account_id, consumer_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = consumerId });
        command.Parameters.Add(new NpgsqlParameter { Value = seq });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit recovery rewinds the durable cursor and bumps its generation. The new
    /// generation is returned so the connection can keep acknowledging on the new epoch; any
    /// writer still holding the old generation is silently ignored.
    /// </summary>
    public async Task<long> RewindConsumerCursorAsync(
        Guid accountId, string consumerId, long seq, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {S}.event_cursor (account_id, consumer_id, last_seq, epoch)
            VALUES ($1, $2, $3, 1)
            ON CONFLICT (account_id, consumer_id) DO UPDATE
                SET last_seq = LEAST({S}.event_cursor.last_seq, EXCLUDED.last_seq),
                    epoch = {S}.event_cursor.epoch + 1,
                    updated_at = now()
            RETURNING epoch
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { Value = consumerId });
        command.Parameters.Add(new NpgsqlParameter { Value = seq });
        var epoch = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return epoch is long value ? value : 0;
    }
}

/// <summary>Durable consumer cursor and its generation.</summary>
public sealed record ConsumerCursor(long Seq, long Epoch);
