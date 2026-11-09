using Microsoft.Extensions.Configuration;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Host;

public sealed class MilkyOptions
{
    public required Uri BackendBaseAddress { get; init; }
    public required string ServerDomain { get; init; }
    public string? CertificatePin { get; init; }
    public bool LargeClientHello { get; init; }
    public bool AllowPlaintextBackend { get; init; }
    public TimeSpan BackendTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public required string ConnectionString { get; init; }
    public required string SecretKey { get; init; }

    public required string ListenUrl { get; init; }
    public required string AccessToken { get; init; }

    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProfileCacheTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan OperationLease { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationWait { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int ReplayBatchSize { get; init; } = 200;
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan MediaTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public bool AutoAnswer { get; init; }
    public bool BackgroundSync { get; init; } = true;
    public bool BackgroundSendReconcile { get; init; } = true;
    public bool BackgroundCallMaintenance { get; init; } = true;
    public int MediaMaxAttempts { get; init; } = 6;
    public int SignalingQueueCapacity { get; init; } = 64;
    public int SignalingMaxAttempts { get; init; } = 5;
    public TimeSpan SignalingRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SignalingMaxAge { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>"none" (default), "loopback" (host tests only) or "tgcalls" (native agent).</summary>
    public string MediaEngine { get; init; } = "none";

    /// <summary>Command that starts the native media agent, e.g. "/opt/milky/milky_media_agent --agent".</summary>
    public string? MediaAgentCommand { get; init; }
    public TimeSpan MediaAgentStartup { get; init; } = TimeSpan.FromSeconds(10);
    public bool MediaAgentLog { get; init; }
    public int MaxPoolSize { get; init; } = 20;

    /// <summary>Web management is disabled until a deployment secret is explicitly supplied.</summary>
    public string? ManagementSecret { get; init; }
    public TimeSpan ManagementSessionLifetime { get; init; } = TimeSpan.FromHours(8);
    public bool ManagementSecureCookie { get; init; }
    public SameSiteMode ManagementCookieSameSite { get; init; } = SameSiteMode.Strict;
    public string[] AllowedOrigins { get; init; } = [];
    public int ManagementLogCapacity { get; init; } = 1000;

    /// <summary>Version loaded from PostgreSQL before this process built its runtime services.</summary>
    public long AppliedConfigVersion { get; init; }

    public static MilkyOptions Load(IConfiguration configuration)
    {
        var backend = configuration.GetSection("backend");
        var storage = configuration.GetSection("storage");
        var milky = configuration.GetSection("milky");
        var runtime = configuration.GetSection("runtime");
        var admin = configuration.GetSection("admin");

        var baseAddress = backend["base_address"]
            ?? throw new InvalidOperationException("backend.base_address is required");
        var serverDomain = backend["server_domain"]
            ?? throw new InvalidOperationException("backend.server_domain is required");
        var connectionString = storage["connection_string"]
            ?? throw new InvalidOperationException("storage.connection_string is required");
        var secret = storage["secret_key"]
            ?? throw new InvalidOperationException("storage.secret_key is required (base64, 32 bytes)");
        var accessToken = milky["access_token"]
            ?? throw new InvalidOperationException("milky.access_token is required");
        if (accessToken.Length < 16)
            throw new InvalidOperationException("milky.access_token must be at least 16 characters");
        var managementSecret = admin["secret"];
        if (managementSecret is { Length: < 16 })
            throw new InvalidOperationException("admin.secret must be at least 16 characters when configured");

        var allowedOrigins = configuration.GetSection("cors:allowed_origins").Get<string[]>() ?? [];
        allowedOrigins = allowedOrigins.Select(value =>
        {
            if (value is null || value.Contains('*')
                || !Uri.TryCreate(value, UriKind.Absolute, out var origin)
                || origin.Scheme is not ("http" or "https")
                || origin.AbsolutePath != "/" || origin.Query.Length != 0
                || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new InvalidOperationException("cors.allowed_origins must contain explicit HTTP(S) origins without paths or wildcards");
            return origin.GetLeftPart(UriPartial.Authority);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!Enum.TryParse<SameSiteMode>(admin["cookie_same_site"] ?? "Strict", true, out var sameSite)
            || sameSite is not (SameSiteMode.Strict or SameSiteMode.Lax or SameSiteMode.None))
            throw new InvalidOperationException("admin.cookie_same_site must be Strict, Lax or None");
        if (sameSite == SameSiteMode.None && !admin.GetValue("secure_cookie", false))
            throw new InvalidOperationException("admin.cookie_same_site=None requires admin.secure_cookie=true and HTTPS");

        return new MilkyOptions
        {
            BackendBaseAddress = new Uri(baseAddress, UriKind.Absolute),
            ServerDomain = serverDomain,
            CertificatePin = backend["certificate_sha256_pin"],
            LargeClientHello = backend.GetValue("large_clienthello", false),
            AllowPlaintextBackend = backend.GetValue("allow_plaintext", false),
            BackendTimeout = TimeSpan.FromSeconds(backend.GetValue("request_timeout_seconds", 30)),
            ConnectionString = connectionString,
            SecretKey = secret,
            ListenUrl = milky["listen"] ?? "http://127.0.0.1:8080",
            AccessToken = accessToken,
            SyncInterval = TimeSpan.FromSeconds(runtime.GetValue("sync_interval_seconds", 5)),
            OutboxRetention = TimeSpan.FromHours(runtime.GetValue("outbox_retention_hours", 6)),
            ProfileCacheTtl = TimeSpan.FromMinutes(runtime.GetValue("profile_cache_ttl_minutes", 10)),
            OperationLease = TimeSpan.FromSeconds(runtime.GetValue("operation_lease_seconds", 30)),
            OperationWait = TimeSpan.FromSeconds(runtime.GetValue("operation_wait_seconds", 10)),
            ReconcileInterval = TimeSpan.FromSeconds(runtime.GetValue("reconcile_interval_seconds", 10)),
            ReplayBatchSize = runtime.GetValue("replay_batch_size", 200),
            MediaTokenLifetime = TimeSpan.FromMinutes(runtime.GetValue("media_token_lifetime_minutes", 10)),
            AutoAnswer = runtime.GetValue("auto_answer", false),
            BackgroundSync = runtime.GetValue("background_sync", true),
            BackgroundSendReconcile = runtime.GetValue("background_send_reconcile", true),
            BackgroundCallMaintenance = runtime.GetValue("background_call_maintenance", true),
            MediaMaxAttempts = runtime.GetValue("media_max_attempts", 6),
            SignalingQueueCapacity = Math.Clamp(runtime.GetValue("signaling_queue_capacity", 64), 1, 1024),
            SignalingMaxAttempts = Math.Clamp(runtime.GetValue("signaling_max_attempts", 5), 1, 20),
            SignalingRetryBaseDelay = TimeSpan.FromSeconds(Math.Clamp(
                runtime.GetValue("signaling_retry_base_seconds", 1), 1, 60)),
            SignalingMaxAge = TimeSpan.FromSeconds(Math.Clamp(
                runtime.GetValue("signaling_max_age_seconds", 60), 1, 600)),
            MediaEngine = configuration.GetSection("media")["engine"] ?? "none",
            MediaAgentCommand = configuration.GetSection("media")["agent_command"],
            MediaAgentStartup = TimeSpan.FromSeconds(configuration.GetSection("media").GetValue("agent_startup_seconds", 10)),
            MediaAgentLog = configuration.GetSection("media").GetValue("agent_log", false),
            MaxPoolSize = storage.GetValue("max_pool_size", 20),
            AllowedOrigins = allowedOrigins,
            ManagementCookieSameSite = sameSite,
            ManagementSecret = managementSecret,
            ManagementSessionLifetime = TimeSpan.FromMinutes(admin.GetValue("session_lifetime_minutes", 480)),
            ManagementSecureCookie = admin.GetValue("secure_cookie", false),
            ManagementLogCapacity = Math.Clamp(admin.GetValue("log_capacity", 1000), 16, 5000),
        };
    }

    public static IConfigurationRoot LoadConfiguration(string contentRoot)
        => new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("config/local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NYARUGRAM_")
            .Build();

    /// <summary>
    /// Applies the versioned Web-management row loaded from PostgreSQL before the host is
    /// built. Values are startup-bound, so a saved row takes effect on the next restart.
    /// </summary>
    public MilkyOptions WithAdminConfig(AdminConfigSnapshot snapshot) => new()
    {
        BackendBaseAddress = BackendBaseAddress,
        ServerDomain = ServerDomain,
        CertificatePin = CertificatePin,
        LargeClientHello = LargeClientHello,
        AllowPlaintextBackend = AllowPlaintextBackend,
        BackendTimeout = BackendTimeout,
        ConnectionString = ConnectionString,
        SecretKey = SecretKey,
        ListenUrl = ListenUrl,
        AccessToken = snapshot.AccessTokenOverride ?? AccessToken,
        SyncInterval = snapshot.SyncIntervalSeconds is { } sync
            ? TimeSpan.FromSeconds(sync)
            : SyncInterval,
        ProfileCacheTtl = snapshot.ProfileCacheTtlMinutes is { } profile
            ? TimeSpan.FromMinutes(profile)
            : ProfileCacheTtl,
        OperationLease = OperationLease,
        OperationWait = OperationWait,
        ReconcileInterval = ReconcileInterval,
        ReplayBatchSize = ReplayBatchSize,
        OutboxRetention = snapshot.OutboxRetentionHours is { } retentionHours
            ? TimeSpan.FromHours(retentionHours)
            : OutboxRetention,
        MediaTokenLifetime = MediaTokenLifetime,
        AutoAnswer = snapshot.AutoAnswer ?? AutoAnswer,
        BackgroundSync = BackgroundSync,
        BackgroundSendReconcile = BackgroundSendReconcile,
        BackgroundCallMaintenance = BackgroundCallMaintenance,
        MediaMaxAttempts = MediaMaxAttempts,
        SignalingQueueCapacity = SignalingQueueCapacity,
        SignalingMaxAttempts = SignalingMaxAttempts,
        SignalingRetryBaseDelay = SignalingRetryBaseDelay,
        SignalingMaxAge = SignalingMaxAge,
        MediaEngine = MediaEngine,
        MediaAgentCommand = MediaAgentCommand,
        MediaAgentStartup = MediaAgentStartup,
        MediaAgentLog = MediaAgentLog,
        MaxPoolSize = MaxPoolSize,
        AllowedOrigins = AllowedOrigins,
        ManagementCookieSameSite = ManagementCookieSameSite,
        ManagementSecret = ManagementSecret,
        ManagementSessionLifetime = ManagementSessionLifetime,
        ManagementSecureCookie = ManagementSecureCookie,
        ManagementLogCapacity = ManagementLogCapacity,
        AppliedConfigVersion = snapshot.Version,
    };
}
