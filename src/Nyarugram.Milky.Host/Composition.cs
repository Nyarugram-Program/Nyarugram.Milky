using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Media;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Host;

/// <summary>
/// Explicit composition root. Storage is PostgreSQL-only; the backend transport is
/// Protobuf-only; the media engine is chosen by configuration and defaults to none.
/// </summary>
public static class Composition
{
    public static void Register(IServiceCollection services, MilkyOptions options, string contentRoot)
    {
        services.AddSingleton(options);

        services.AddSingleton(new MilkyStorageOptions
        {
            ConnectionString = options.ConnectionString,
            SecretKeyBase64 = options.SecretKey,
            MaxPoolSize = options.MaxPoolSize,
        });
        services.AddSingleton<MilkyDatabase>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AccountStore>();
        services.AddSingleton<AdminConfigStore>();
        services.AddSingleton<AdminAuthService>();
        services.AddSingleton<IdentityStore>();
        services.AddSingleton<IdempotencyStore>();
        services.AddSingleton<OutboxStore>();
        services.AddSingleton<CallStore>();
        services.AddSingleton<SyncStore>();

        services.AddSingleton(new BackendOptions
        {
            BaseAddress = options.BackendBaseAddress,
            ServerDomain = options.ServerDomain,
            CertificateSha256Pin = options.CertificatePin,
            LargeClientHello = options.LargeClientHello,
            AllowPlaintext = options.AllowPlaintextBackend,
            RequestTimeout = options.BackendTimeout,
        });
        services.AddSingleton<BackendClient>();

        services.AddSingleton<MilkyRuntime>();
        services.AddSingleton<BackendRpc>();
        services.AddSingleton<AccountApi>();
        services.AddSingleton(new RuntimeOptions
        {
            SyncInterval = options.SyncInterval,
            OutboxRetention = options.OutboxRetention,
            MediaTokenLifetime = options.MediaTokenLifetime,
            AutoAnswer = options.AutoAnswer,
            ReplayBatchSize = options.ReplayBatchSize,
            OperationLease = options.OperationLease,
            OperationWait = options.OperationWait,
            ReconcileInterval = options.ReconcileInterval,
            ProfileCacheTtl = options.ProfileCacheTtl,
            BackgroundSync = options.BackgroundSync,
            BackgroundPresence = options.BackgroundPresence,
            PresenceInterval = options.PresenceInterval,
            BackgroundSendReconcile = options.BackgroundSendReconcile,
            BackgroundCallMaintenance = options.BackgroundCallMaintenance,
            MediaMaxAttempts = options.MediaMaxAttempts,
            SignalingQueueCapacity = options.SignalingQueueCapacity,
            SignalingMaxAttempts = options.SignalingMaxAttempts,
            SignalingRetryBaseDelay = options.SignalingRetryBaseDelay,
            SignalingMaxAge = options.SignalingMaxAge,
        });
        services.AddSingleton(sp => new ProfileCache(sp.GetRequiredService<RuntimeOptions>().ProfileCacheTtl));
        services.AddSingleton<IdentityResolver>();
        services.AddSingleton<GroupMemberDirectory>();
        services.AddSingleton<MessageMapper>();
        services.AddSingleton<SendService>();
        services.AddSingleton(ProtocolLocks.Load(contentRoot));

        if (string.Equals(options.MediaEngine, "loopback", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<LoopbackMediaEngine>();
            services.AddSingleton<IMediaEngine>(sp => sp.GetRequiredService<LoopbackMediaEngine>());
        }
        else if (string.Equals(options.MediaEngine, "tgcalls", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(sp => new TgcallsMediaEngine(
                options.MediaAgentCommand, options.MediaAgentStartup, options.MediaAgentLog));
            services.AddSingleton<IMediaEngine>(sp => sp.GetRequiredService<TgcallsMediaEngine>());
        }
        else
        {
            services.AddSingleton<IMediaEngine, UnavailableMediaEngine>();
        }
        services.AddSingleton<IDiffieHellman, BigIntegerDiffieHellman>();

        services.AddSingleton<EventBus>();
        services.AddSingleton<SyncSignal>();
        services.AddSingleton<CallService>();
        services.AddSingleton<ICallUpdateSink>(sp => sp.GetRequiredService<CallService>());
        services.AddSingleton<MilkyActions>();
        services.AddSingleton<SessionManager>();

        services.AddSingleton<SyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncService>());
        services.AddHostedService<EventHintListener>();
        services.AddSingleton<PresenceReporter>();
        services.AddHostedService(sp => sp.GetRequiredService<PresenceReporter>());
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<SendReconciler>();
        services.AddHostedService<CallMaintenanceWorker>();
        services.AddHostedService<CallFaultFinalizer>();
    }
}
