using Npgsql;

namespace Nyarugram.Milky.Storage;

public sealed record OutboxMessage(string EventId, string EventType, string PayloadJson);

/// <summary>
/// Commits one synchronisation page atomically: message projection, call projection,
/// pending events and the new cursor land together. The cursor only advances once the
/// rows it depends on are durable.
/// </summary>
public sealed class SyncStore
{
    private readonly MilkyDatabase _db;

    public SyncStore(MilkyDatabase db) => _db = db;

    private const string S = MilkyDatabase.SchemaName;

    public async Task CommitAsync(
        Guid accountId,
        IReadOnlyList<MessageRow> messages,
        IReadOnlyList<(long MessageId, string Reason)> skips,
        IReadOnlyList<OutboxMessage> events,
        SyncCursor cursor,
        CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (messages.Count > 0)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {S}.message (account_id, message_id, peer_kind, peer_uin, sender_uin, created_at_ms, outgoing)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (account_id, message_id) DO NOTHING
                """, connection, transaction);
            foreach (var row in messages)
            {
                insert.Parameters.Clear();
                insert.Parameters.Add(new NpgsqlParameter { Value = accountId });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.MessageId });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.PeerKind == PeerKind.Group ? "group" : "friend" });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.PeerUin });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.SenderUin });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.CreatedAtMs });
                insert.Parameters.Add(new NpgsqlParameter { Value = row.Outgoing });
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var (messageId, reason) in skips)
            await IdentityStore.RecordSkipAsync(connection, transaction, accountId, messageId, reason, ct)
                .ConfigureAwait(false);

        foreach (var message in events)
        {
            await using var enqueue = new NpgsqlCommand($"""
                INSERT INTO {S}.outbox (account_id, event_id, event_type, payload)
                VALUES ($1, $2, $3, $4::jsonb)
                ON CONFLICT (account_id, event_id) DO NOTHING
                """, connection, transaction);
            enqueue.Parameters.Add(new NpgsqlParameter { Value = accountId });
            enqueue.Parameters.Add(new NpgsqlParameter { Value = message.EventId });
            enqueue.Parameters.Add(new NpgsqlParameter { Value = message.EventType });
            enqueue.Parameters.Add(new NpgsqlParameter { Value = message.PayloadJson });
            await enqueue.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var save = new NpgsqlCommand($"""
            INSERT INTO {S}.sync_state (account_id, pts, qts, seq, date_ms, initialized, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, now())
            ON CONFLICT (account_id) DO UPDATE
                SET pts = EXCLUDED.pts, qts = EXCLUDED.qts, seq = EXCLUDED.seq,
                    date_ms = EXCLUDED.date_ms, initialized = EXCLUDED.initialized, updated_at = now()
                WHERE {S}.sync_state.pts <= EXCLUDED.pts
            """, connection, transaction))
        {
            save.Parameters.Add(new NpgsqlParameter { Value = accountId });
            save.Parameters.Add(new NpgsqlParameter { Value = cursor.Pts });
            save.Parameters.Add(new NpgsqlParameter { Value = cursor.Qts });
            save.Parameters.Add(new NpgsqlParameter { Value = cursor.Seq });
            save.Parameters.Add(new NpgsqlParameter { Value = cursor.DateMs });
            save.Parameters.Add(new NpgsqlParameter { Value = cursor.Initialized });
            await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Resets the cursor after a too-long difference so the next tick re-baselines.</summary>
    public async Task ResetCursorAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {S}.sync_state SET pts = 0, initialized = false, updated_at = now()
            WHERE account_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
