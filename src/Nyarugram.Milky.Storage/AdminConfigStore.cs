using System.Security.Cryptography;
using Npgsql;

namespace Nyarugram.Milky.Storage;

/// <summary>
/// Versioned Web-management overrides. Deployment secrets in config/local.json stay the
/// baseline; only the Milky access token can be overridden here, encrypted with the same
/// adapter key used for stored backend sessions.
/// </summary>
public sealed record AdminConfigSnapshot(
    long Version,
    int? SyncIntervalSeconds,
    int? OutboxRetentionHours,
    int? ProfileCacheTtlMinutes,
    bool? AutoAnswer,
    bool HasAccessTokenOverride,
    string? AccessTokenOverride)
{
    public static AdminConfigSnapshot Empty { get; } =
        new(0, null, null, null, null, false, null);
}

public sealed record AdminConfigValues(
    int SyncIntervalSeconds,
    int OutboxRetentionHours,
    int ProfileCacheTtlMinutes,
    bool AutoAnswer);

public enum AdminSecretUpdateKind
{
    Keep,
    Replace,
    Clear,
}

public sealed record AdminSecretUpdate(AdminSecretUpdateKind Kind, string? Value);

public sealed class AdminConfigConflictException : Exception
{
    public AdminConfigConflictException(long currentVersion)
        : base($"configuration version changed to {currentVersion}") =>
        CurrentVersion = currentVersion;

    public long CurrentVersion { get; }
}

public sealed class AdminConfigStore
{
    private const string S = MilkyDatabase.SchemaName;

    private readonly MilkyDatabase _db;
    private readonly SecretProtector _protector;

    public AdminConfigStore(MilkyDatabase db, SecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<AdminConfigSnapshot> LoadAsync(CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await LoadAsync(connection, null, ct).ConfigureAwait(false);
    }

    public async Task<AdminConfigSnapshot> SaveAsync(
        long expectedVersion,
        AdminConfigValues values,
        AdminSecretUpdate accessToken,
        CancellationToken ct)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Create the singleton row first so SELECT ... FOR UPDATE always has a row to lock.
        await using (var ensure = new NpgsqlCommand($"""
            INSERT INTO {S}.admin_config (singleton, version)
            VALUES (1, 0)
            ON CONFLICT (singleton) DO NOTHING
            """, connection, transaction))
            await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var current = await ReadRowAsync(connection, transaction, ct).ConfigureAwait(false);
        if (current.Version != expectedVersion)
            throw new AdminConfigConflictException(current.Version);

        byte[]? cipher = current.Cipher;
        byte[]? nonce = current.Nonce;
        byte[]? tag = current.Tag;
        switch (accessToken.Kind)
        {
            case AdminSecretUpdateKind.Replace:
                if (string.IsNullOrEmpty(accessToken.Value))
                    throw new InvalidOperationException("replacement access token is empty");
                var protectedValue = _protector.Protect(accessToken.Value);
                cipher = protectedValue.Cipher;
                nonce = protectedValue.Nonce;
                tag = protectedValue.Tag;
                break;
            case AdminSecretUpdateKind.Clear:
                cipher = null;
                nonce = null;
                tag = null;
                break;
        }

        await using (var update = new NpgsqlCommand($"""
            UPDATE {S}.admin_config SET
                version = version + 1,
                sync_interval_seconds = $1,
                outbox_retention_hours = $2,
                profile_cache_ttl_minutes = $3,
                auto_answer = $4,
                access_token_cipher = $5,
                access_token_nonce = $6,
                access_token_tag = $7,
                updated_at = now()
            WHERE singleton = 1
            RETURNING version
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = values.SyncIntervalSeconds });
            update.Parameters.Add(new NpgsqlParameter { Value = values.OutboxRetentionHours });
            update.Parameters.Add(new NpgsqlParameter { Value = values.ProfileCacheTtlMinutes });
            update.Parameters.Add(new NpgsqlParameter { Value = values.AutoAnswer });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)cipher ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)nonce ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)tag ?? DBNull.Value });
            await update.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        // Re-read so a "keep" operation returns the decrypted effective override and the
        // exact persisted row rather than a locally reconstructed approximation.
        return await LoadAsync(ct).ConfigureAwait(false);
    }

    private async Task<AdminConfigSnapshot> LoadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var row = await ReadRowAsync(connection, transaction, ct).ConfigureAwait(false);
        string? accessToken = null;
        if (row.Cipher is not null && row.Nonce is not null && row.Tag is not null)
        {
            try
            {
                accessToken = _protector.Unprotect(row.Cipher, row.Nonce, row.Tag);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "stored admin access-token override cannot be decrypted; storage.secret_key may have changed", ex);
            }
        }
        return new AdminConfigSnapshot(
            row.Version,
            row.SyncIntervalSeconds,
            row.OutboxRetentionHours,
            row.ProfileCacheTtlMinutes,
            row.AutoAnswer,
            row.Cipher is not null,
            accessToken);
    }

    private async Task<Row> ReadRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT version, sync_interval_seconds, outbox_retention_hours,
                   profile_cache_ttl_minutes, auto_answer,
                   access_token_cipher, access_token_nonce, access_token_tag
            FROM {S}.admin_config
            WHERE singleton = 1
            FOR UPDATE
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return new Row(0, null, null, null, null, null, null, null);
        return new Row(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7));
    }

    private sealed record Row(
        long Version,
        int? SyncIntervalSeconds,
        int? OutboxRetentionHours,
        int? ProfileCacheTtlMinutes,
        bool? AutoAnswer,
        byte[]? Cipher,
        byte[]? Nonce,
        byte[]? Tag);
}
