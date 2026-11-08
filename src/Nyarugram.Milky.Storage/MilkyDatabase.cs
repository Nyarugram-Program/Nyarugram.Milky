using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Nyarugram.Milky.Storage;

public sealed class MilkyStorageOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>Base64 of a 32-byte key. Must live outside the database (config or key store).</summary>
    public required string SecretKeyBase64 { get; init; }

    public int MaxPoolSize { get; init; } = 20;
}

/// <summary>AES-256-GCM protection for backend bearer tokens stored in PostgreSQL.</summary>
public sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(MilkyStorageOptions options)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(options.SecretKeyBase64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("storage.secret_key must be base64");
        }
        if (key.Length != 32)
            throw new InvalidOperationException("storage.secret_key must decode to exactly 32 bytes (AES-256)");
        _key = key;
    }

    public (byte[] Cipher, byte[] Nonce, byte[] Tag) Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, bytes, cipher, tag);
        CryptographicOperations.ZeroMemory(bytes);
        return (cipher, nonce, tag);
    }

    public string Unprotect(byte[] cipher, byte[] nonce, byte[] tag)
    {
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("stored credential could not be decrypted; check storage.secret_key", ex);
        }
        return Encoding.UTF8.GetString(plain);
    }
}

public sealed class MilkyDatabase : IAsyncDisposable
{
    /// <summary>Schema holding adapter state. Fixed so migrations and queries agree.</summary>
    public const string SchemaName = "nyarugram_milky";

    private readonly MilkyStorageOptions _options;
    private readonly ILogger<MilkyDatabase> _logger;
    private readonly NpgsqlDataSource _dataSource;

    public MilkyDatabase(MilkyStorageOptions options, ILogger<MilkyDatabase> logger)
    {
        _options = options;
        _logger = logger;
        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = options.MaxPoolSize;
        _dataSource = builder.Build();
    }

    public NpgsqlDataSource DataSource => _dataSource;
    public string Schema => SchemaName;

    /// <summary>
    /// Applies migrations. When the configured database does not exist yet it is created from
    /// the maintenance database, so a fresh deployment does not need an external client tool.
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await EnsureDatabaseAsync(ct).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Serialize migrations across instances without depending on a lock table.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(884422001)", connection))
            await lockCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        try
        {
            await using (var schema = new NpgsqlCommand(
                $"CREATE SCHEMA IF NOT EXISTS {SchemaName}", connection))
                await schema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using (var table = new NpgsqlCommand($"""
                CREATE TABLE IF NOT EXISTS {SchemaName}.schema_migration (
                    version integer PRIMARY KEY,
                    name text NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, connection))
                await table.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var applied = new HashSet<int>();
            await using (var query = new NpgsqlCommand($"SELECT version FROM {SchemaName}.schema_migration", connection))
            await using (var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    applied.Add(reader.GetInt32(0));

            foreach (var migration in MigrationScripts.All)
            {
                if (applied.Contains(migration.Version)) continue;
                _logger.LogInformation("applying migration {Version} {Name}", migration.Version, migration.Name);
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await using (var record = new NpgsqlCommand(
                    $"INSERT INTO {SchemaName}.schema_migration(version, name) VALUES ($1, $2)", connection, transaction))
                {
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Version });
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Name });
                    await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(884422001)", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString);
        var database = builder.Database;
        if (string.IsNullOrEmpty(database)) return;
        try
        {
            await using var probe = new NpgsqlConnection(_options.ConnectionString);
            await probe.OpenAsync(ct).ConfigureAwait(false);
            return;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            _logger.LogInformation("database {Database} does not exist; creating it", database);
        }

        builder.Database = "postgres";
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
        await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is 1;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

public sealed record MigrationScript(int Version, string Name, string Sql)
{
    public static MigrationScript FromResource(string name, string sql)
    {
        var file = name.Split('.').Reverse().Skip(1).FirstOrDefault() ?? "0000";
        var version = int.TryParse(file.Split('_')[0], out var parsed) ? parsed : 0;
        return new MigrationScript(version, name, sql);
    }
}

public static class MigrationScripts
{
    public static IReadOnlyList<MigrationScript> All { get; } = Load();

    private static List<MigrationScript> Load()
    {
        var assembly = typeof(MigrationScripts).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"missing migration resource {resource}");
            using var reader = new StreamReader(stream);
            scripts.Add(MigrationScript.FromResource(resource, reader.ReadToEnd()));
        }
        return scripts;
    }
}
