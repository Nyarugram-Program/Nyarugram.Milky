using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "serve";
        var contentRoot = Environment.GetEnvironmentVariable("NYARUGRAM_CONTENT_ROOT")
            ?? Directory.GetCurrentDirectory();

        try
        {
            return command switch
            {
                "serve" => await ServeAsync(args, contentRoot).ConfigureAwait(false),
                "init" => Init(contentRoot),
                "login" => await LoginAsync(contentRoot).ConfigureAwait(false),
                "logout" => await LogoutAsync(contentRoot).ConfigureAwait(false),
                "status" => await StatusAsync(contentRoot).ConfigureAwait(false),
                "version" => PrintVersion(),
                _ => Usage(command),
            };
        }
        catch (MilkyActionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Backend.BackendException or Npgsql.NpgsqlException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        Console.Error.WriteLine("""
            usage: nyarugram-milky <command>

              init      create config/local.json with generated secrets
              login     authenticate against the backend and store the session
              serve     run the Milky HTTP/event service (default)
              status    print account, cursor and queue state
              logout    revoke the stored session
              version   print the adapter and contract versions
            """);
        return 2;
    }

    private static int PrintVersion()
    {
        var root = Environment.GetEnvironmentVariable("NYARUGRAM_CONTENT_ROOT") ?? Directory.GetCurrentDirectory();
        var locks = ProtocolLocks.Load(root);
        Console.WriteLine($"nyarugram-milky {ProtocolLocks.ImplementationVersion}");
        Console.WriteLine($"milky {locks.Milky.Version} (schema sha256 {locks.Milky.Sha256})");
        Console.WriteLine($"backend protocol major {locks.BackendProtocol.ProtocolMajor} (proto sha256 {locks.BackendProtocol.Sha256})");
        return 0;
    }

    // ------------------------------------------------------------------- serve

    private static async Task<int> ServeAsync(string[] args, string contentRoot)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("config/local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NYARUGRAM_")
            .AddCommandLine(args)
            .Build();

        var baseOptions = MilkyOptions.Load(configuration);
        // The management row lives in PostgreSQL, so it must be loaded before the host
        // services capture startup-bound values such as the Milky access token.
        var applied = await LoadAdminConfigAsync(baseOptions, CancellationToken.None).ConfigureAwait(false);
        var options = baseOptions.WithAdminConfig(applied);
        var app = CreateApp(options, contentRoot, args);

        var database = app.Services.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);

        var sessions = app.Services.GetRequiredService<SessionManager>();
        var runtime = app.Services.GetRequiredService<MilkyRuntime>();
        var restored = await sessions.RestoreAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        if (restored is null)
            app.Logger.LogWarning("no active session; Milky calls will return -403 until 'login' succeeds");
        else
            await app.Services.GetRequiredService<CallService>()
                .ReconcileAsync(restored, app.Lifetime.ApplicationStopping).ConfigureAwait(false);

        app.Logger.LogInformation(
            "listening on {Url}; backend {Backend}; media engine {Engine}",
            options.ListenUrl, options.BackendBaseAddress, app.Services.GetRequiredService<IMediaEngine>().Capability.Engine);

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<AdminConfigSnapshot> LoadAdminConfigAsync(
        MilkyOptions options, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(new MilkyStorageOptions
        {
            ConnectionString = options.ConnectionString,
            SecretKeyBase64 = options.SecretKey,
            MaxPoolSize = Math.Min(options.MaxPoolSize, 4),
        });
        services.AddSingleton<MilkyDatabase>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AdminConfigStore>();

        await using var provider = services.BuildServiceProvider();
        var database = provider.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(ct).ConfigureAwait(false);
        return await provider.GetRequiredService<AdminConfigStore>()
            .LoadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the web host without starting it, so integration tests exercise the exact
    /// same endpoint wiring and services as production.
    /// </summary>
    public static WebApplication CreateApp(MilkyOptions options, string contentRoot, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            Args = args ?? [],
        });
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });

        // The bounded log buffer is both an ILoggerProvider and an injected management
        // dependency; register the same instance for both roles.
        var adminLogBuffer = new AdminLogBuffer(options);
        builder.Logging.AddProvider(adminLogBuffer);
        builder.Services.AddSingleton(adminLogBuffer);

        Composition.Register(builder.Services, options, contentRoot);
        builder.WebHost.UseUrls(options.ListenUrl);

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(adminLogBuffer.Dispose);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        var database = app.Services.GetRequiredService<MilkyDatabase>();
        app.Lifetime.ApplicationStarted.Register(() => { });
        var endpoints = ActivatorUtilities.CreateInstance<MilkyEndpoints>(app.Services);

        app.MapPost("/api/{action}", (HttpContext context, string action) =>
            endpoints.HandleApiAsync(context, action, context.RequestAborted));
        app.MapMethods("/event", ["GET"], (HttpContext context) =>
            endpoints.HandleEventAsync(context, context.RequestAborted));
        app.MapMethods("/nyarugram/calls/{callId}/media", ["GET"],
            (HttpContext context, string callId) => endpoints.HandleMediaAsync(context, callId, context.RequestAborted));
        app.MapGet("/health", (HttpContext context) =>
            endpoints.HandleHealthAsync(context, database, context.RequestAborted));

        AdminEndpoints.Map(app);
    }

    // -------------------------------------------------------------------- init

    private static int Init(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "config", "local.json");
        Directory.CreateDirectory(Path.Combine(contentRoot, "config"));
        if (File.Exists(path))
        {
            Console.WriteLine($"config already exists: {path}");
            return 0;
        }

        var template = new
        {
            backend = new
            {
                base_address = "https://192.168.1.20:8521",
                server_domain = "example.local",
                certificate_sha256_pin = "",
                allow_plaintext = false,
                request_timeout_seconds = 30,
            },
            storage = new
            {
                connection_string = "Host=127.0.0.1;Port=5432;Database=nyarugram_milky;Username=nyarugram;Password=change-me",
                secret_key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                max_pool_size = 20,
            },
            milky = new
            {
                listen = "http://127.0.0.1:8080",
                access_token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
            },
            media = new { engine = "none" },
            runtime = new
            {
                sync_interval_seconds = 5,
                outbox_retention_hours = 6,
                media_token_lifetime_minutes = 10,
                auto_answer = false,
                profile_cache_ttl_minutes = 10,
                operation_lease_seconds = 30,
                operation_wait_seconds = 10,
                reconcile_interval_seconds = 10,
                replay_batch_size = 200,
                background_sync = true,
                background_send_reconcile = true,
                background_call_maintenance = true,
                media_max_attempts = 6,
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {path}");
        Console.WriteLine("replace backend.base_address, backend.server_domain and storage.connection_string before login");
        Console.WriteLine("this file holds the credential-protection key; keep it outside the database backup and restrict file access");
        return 0;
    }

    // ------------------------------------------------------------------- login

    private static async Task<int> LoginAsync(string contentRoot)
    {
        await using var provider = Build(contentRoot, out var options);

        var database = provider.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        Console.Write("backend username: ");
        var username = Console.ReadLine()?.Trim() ?? "";
        var password = ReadSecret("password: ");
        Console.Write("authenticator code (blank if disabled): ");
        var totp = Console.ReadLine()?.Trim();

        var sessions = provider.GetRequiredService<SessionManager>();
        var context = await sessions.LoginAsync(
            username, password, string.IsNullOrWhiteSpace(totp) ? null : totp, CancellationToken.None)
            .ConfigureAwait(false);

        await provider.GetRequiredService<CallService>().ReconcileAsync(context, CancellationToken.None)
            .ConfigureAwait(false);

        Console.WriteLine($"logged in: uin {context.Account.Uin} ({context.Account.DisplayName}) generation {context.Generation}");
        return 0;
    }

    private static async Task<int> LogoutAsync(string contentRoot)
    {
        await using var provider = Build(contentRoot, out _);
        var database = provider.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);
        var sessions = provider.GetRequiredService<SessionManager>();
        await sessions.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        await sessions.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("logged out");
        return 0;
    }

    private static async Task<int> StatusAsync(string contentRoot)
    {
        await using var provider = Build(contentRoot, out var options);

        var database = provider.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        var accounts = provider.GetRequiredService<AccountStore>();
        var account = await accounts.GetSingleAccountAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"backend      {options.BackendBaseAddress} ({options.ServerDomain})");
        if (account is null)
        {
            Console.WriteLine("account      none (run 'login')");
            return 0;
        }

        var session = await accounts.GetActiveSessionAsync(account.AccountId, CancellationToken.None).ConfigureAwait(false);
        var cursor = await accounts.GetCursorAsync(account.AccountId, CancellationToken.None).ConfigureAwait(false);
        var outbox = provider.GetRequiredService<OutboxStore>();
        var metrics = await outbox.GetMetricsAsync(account.AccountId, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"account      uin {account.Uin} ({account.DisplayName}) uuid {account.AccountUuid}");
        Console.WriteLine(session is null
            ? "session      none (run 'login')"
            : $"session      generation {session.Generation}, expires {session.ExpiresAt:u}");
        Console.WriteLine($"cursor       pts {cursor.Pts}, qts {cursor.Qts}, seq {cursor.Seq}, initialized {cursor.Initialized}");
        Console.WriteLine($"outbox       {metrics.Pending} pending, {metrics.Expired} expired, {metrics.Delivered} delivered");
        if (metrics.OldestPendingAt is { } oldest)
            Console.WriteLine($"outbox oldest pending {oldest:u}");
        return 0;
    }

    private static ServiceProvider Build(string contentRoot, out MilkyOptions options)
    {
        var configuration = MilkyOptions.LoadConfiguration(contentRoot);
        options = MilkyOptions.Load(configuration);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Warning));
        Composition.Register(services, options, contentRoot);
        return services.BuildServiceProvider();
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length == 0) continue;
                builder.Length--;
                Console.Write("\b \b");
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            builder.Append(key.KeyChar);
            Console.Write('*');
        }
        Console.WriteLine();
        return builder.ToString();
    }
}
