using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Host;

/// <summary>
/// Unified Web-management surface. It never proxies arbitrary URLs: every operation is
/// implemented against the adapter's own SessionManager, PostgreSQL state and call service.
/// </summary>
public sealed class AdminEndpoints
{
    private const string ApiVersion = "1";
    private const string AccessTokenPath = "milky.access_token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly AdminAuthService _auth;
    private readonly AdminConfigStore _configs;
    private readonly AdminLogBuffer _logs;
    private readonly MilkyOptions _options;
    private readonly LockFile _locks;
    private readonly MilkyRuntime _runtime;
    private readonly SessionManager _sessions;
    private readonly AccountStore _accounts;
    private readonly MilkyDatabase _database;
    private readonly OutboxStore _outbox;
    private readonly EventBus _events;
    private readonly CallService _calls;
    private readonly ILogger<AdminEndpoints> _logger;

    public AdminEndpoints(
        AdminAuthService auth,
        AdminConfigStore configs,
        AdminLogBuffer logs,
        MilkyOptions options,
        LockFile locks,
        MilkyRuntime runtime,
        SessionManager sessions,
        AccountStore accounts,
        MilkyDatabase database,
        OutboxStore outbox,
        EventBus events,
        CallService calls,
        ILogger<AdminEndpoints> logger)
    {
        _auth = auth;
        _configs = configs;
        _logs = logs;
        _options = options;
        _locks = locks;
        _runtime = runtime;
        _sessions = sessions;
        _accounts = accounts;
        _database = database;
        _outbox = outbox;
        _events = events;
        _calls = calls;
        _logger = logger;
    }

    public static void Map(WebApplication app)
    {
        var endpoints = ActivatorUtilities.CreateInstance<AdminEndpoints>(app.Services);
        var group = app.MapGroup("/admin/v1");

        group.MapGet("/info", (HttpContext context) =>
            endpoints.HandleInfoAsync(context, context.RequestAborted));
        group.MapPost("/session", (HttpContext context) =>
            endpoints.HandleLoginAsync(context, context.RequestAborted));
        group.MapGet("/session", (HttpContext context) =>
            endpoints.HandleGetSessionAsync(context, context.RequestAborted));
        group.MapDelete("/session", (HttpContext context) =>
            endpoints.HandleLogoutAsync(context, context.RequestAborted));

        group.MapGet("/account", (HttpContext context) =>
            endpoints.HandleAccountGetAsync(context, context.RequestAborted));
        group.MapPost("/account/login", (HttpContext context) =>
            endpoints.HandleAccountLoginAsync(context, context.RequestAborted));
        group.MapPost("/account/logout", (HttpContext context) =>
            endpoints.HandleAccountLogoutAsync(context, context.RequestAborted));

        group.MapGet("/config/schema", (HttpContext context) =>
            endpoints.HandleConfigSchemaAsync(context, context.RequestAborted));
        group.MapGet("/config", (HttpContext context) =>
            endpoints.HandleConfigGetAsync(context, context.RequestAborted));
        group.MapGet("/config/status", (HttpContext context) =>
            endpoints.HandleConfigStatusAsync(context, context.RequestAborted));
        group.MapPost("/config/validate", (HttpContext context) =>
            endpoints.HandleConfigValidateAsync(context, context.RequestAborted));
        group.MapPut("/config", (HttpContext context) =>
            endpoints.HandleConfigPutAsync(context, context.RequestAborted));

        group.MapGet("/health", (HttpContext context) =>
            endpoints.HandleHealthAsync(context, context.RequestAborted));
        group.MapGet("/metrics", (HttpContext context) =>
            endpoints.HandleMetricsAsync(context, context.RequestAborted));
        group.MapGet("/logs", (HttpContext context) =>
            endpoints.HandleLogsAsync(context, context.RequestAborted));
        group.MapGet("/logs/stream", (HttpContext context) =>
            endpoints.HandleLogsStreamAsync(context, context.RequestAborted));
    }

    // ------------------------------------------------------------------ session

    private async Task HandleLoginAsync(HttpContext context, CancellationToken ct)
    {
        if (!_auth.OriginMatches(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "origin_rejected",
                "management writes require the same origin or a configured cors.allowed_origins entry", ct).ConfigureAwait(false);
            return;
        }
        if (!_auth.IsConfigured)
        {
            await ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "management_not_configured",
                "admin.secret is not configured in config/local.json", ct).ConfigureAwait(false);
            return;
        }

        var body = await ReadAsync<LoginRequest>(context, ct).ConfigureAwait(false);
        if (body is null)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json",
                "request body must be a JSON object", ct).ConfigureAwait(false);
            return;
        }

        var result = _auth.Login(context, body.Password ?? body.Secret);
        switch (result.Status)
        {
            case AdminLoginStatus.Ok:
                AttachSession(context, result.Session!);
                await DataAsync(context, new
                {
                    authenticated = true,
                    permissions = new[] { "management" },
                    expires_at = result.Session!.ExpiresAt,
                    csrf_token = result.Session.CsrfToken,
                    session_id = result.Session.Id,
                }, ct).ConfigureAwait(false);
                break;
            case AdminLoginStatus.Unauthorized:
                await ErrorAsync(context, StatusCodes.Status401Unauthorized, "management_auth_failed",
                    "management credential is invalid", ct).ConfigureAwait(false);
                break;
            case AdminLoginStatus.RateLimited:
                await ErrorAsync(context, StatusCodes.Status429TooManyRequests, "management_rate_limited",
                    "too many failed attempts; retry later", ct).ConfigureAwait(false);
                break;
            default:
                await ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "management_not_configured",
                    "admin.secret is not configured", ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleGetSessionAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "establish a management session first", ct).ConfigureAwait(false);
            return;
        }
        AttachSession(context, session);
        await DataAsync(context, new
        {
            authenticated = true,
            permissions = new[] { "management" },
            expires_at = session.ExpiresAt,
            csrf_token = session.CsrfToken,
            session_id = session.Id,
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleLogoutAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "no active management session", ct).ConfigureAwait(false);
            return;
        }
        if (!await WriteAllowedAsync(context, session, ct).ConfigureAwait(false)) return;
        _auth.Logout(context);
        await DataAsync(context, new { }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- info

    private async Task HandleInfoAsync(HttpContext context, CancellationToken ct)
    {
        var capability = _calls.Capability;
        var accountSupported = _auth.IsConfigured;
        await DataAsync(context, new
        {
            api_version = ApiVersion,
            instance_id = _runtime.InstanceId,
            adapter_kind = "nyarugram-milky",
            adapter_version = ProtocolLocks.ImplementationVersion,
            protocol_version = _locks.Milky.Version,
            capabilities = new Dictionary<string, object>
            {
                ["account"] = new
                {
                    supported = accountSupported,
                    reason = accountSupported ? null : "admin.secret is not configured",
                },
                ["config"] = new
                {
                    supported = true,
                    reason = "saved values are applied on the next adapter restart",
                },
                ["logs"] = new { supported = true, reason = (string?)null },
                ["metrics"] = new { supported = true, reason = (string?)null },
                ["business_debug"] = new { supported = true, reason = (string?)null },
                ["events"] = new { supported = true, reason = (string?)null },
                ["calls"] = new
                {
                    supported = capability.Available,
                    voice = capability.Audio,
                    video = capability.Video,
                    reason = capability.Reason,
                },
                ["media"] = new
                {
                    supported = capability.Available,
                    engine = capability.Engine,
                    audio = capability.Audio,
                    video = capability.Video,
                    reason = capability.Reason,
                },
            },
            endpoints = new
            {
                management = "/admin/v1",
                business_http = "/",
                events = "/event",
                media = "/nyarugram/calls/{call_id}/media",
            },
        }, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- account

    private async Task HandleAccountGetAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        var current = _runtime.Current;
        if (current is null)
        {
            await DataAsync(context, new
            {
                state = "not_logged_in",
                reason = "no backend account is bound in this process",
            }, ct).ConfigureAwait(false);
            return;
        }
        await DataAsync(context, AccountDto(current), ct).ConfigureAwait(false);
    }

    private async Task HandleAccountLoginAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "no active management session", ct).ConfigureAwait(false);
            return;
        }
        if (!await WriteAllowedAsync(context, session, ct).ConfigureAwait(false)) return;

        var body = await ReadAsync<AccountLoginRequest>(context, ct).ConfigureAwait(false);
        if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request",
                "username and password are required", ct).ConfigureAwait(false);
            return;
        }
        if (body.Username.Length > 128 || body.Password.Length > 512
            || (body.TotpCode?.Length ?? 0) > 32)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request",
                "credential fields exceed the allowed length", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var current = await _sessions.LoginAsync(
                body.Username, body.Password, body.TotpCode, ct).ConfigureAwait(false);
            try
            {
                await _calls.ReconcileAsync(current, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "call reconciliation after account login failed");
            }
            await DataAsync(context, AccountDto(current), ct).ConfigureAwait(false);
        }
        catch (MilkyActionException ex)
        {
            await ErrorAsync(context, MapActionStatus(ex.Code), ex.Code.ToString().ToLowerInvariant(),
                ex.Message, ct).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            _logger.LogWarning("backend account login failed: {Code} {Status}", ex.Code, ex.HttpStatus);
            var status = ex.HttpStatus switch
            {
                401 or 403 => StatusCodes.Status401Unauthorized,
                409 => StatusCodes.Status409Conflict,
                429 => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status502BadGateway,
            };
            await ErrorAsync(context, status, "account_login_failed",
                ex.HttpStatus is 401 or 403
                    ? "backend rejected the account credentials"
                    : "backend account login failed", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "account login failed");
            await ErrorAsync(context, StatusCodes.Status500InternalServerError, "account_login_failed",
                "account login failed; see adapter logs", ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAccountLogoutAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "no active management session", ct).ConfigureAwait(false);
            return;
        }
        if (!await WriteAllowedAsync(context, session, ct).ConfigureAwait(false)) return;

        try
        {
            await _sessions.LogoutAsync(ct).ConfigureAwait(false);
            await DataAsync(context, new { }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "account logout failed");
            await ErrorAsync(context, StatusCodes.Status500InternalServerError, "account_logout_failed",
                "account logout failed; see adapter logs", ct).ConfigureAwait(false);
        }
    }

    private static object AccountDto(SessionContext current) => new
    {
        account_id = current.Account.AccountId.ToString("N"),
        display_name = current.Account.DisplayName,
        username = current.Account.Username,
        gram_id = (string?)null,
        numeric_user_id = current.Account.Uin.ToString(CultureInfo.InvariantCulture),
        generation = current.Generation.ToString(CultureInfo.InvariantCulture),
        avatar = (string?)null,
        state = "active",
        reason = (string?)null,
    };

    // ------------------------------------------------------------------ config

    private async Task HandleConfigSchemaAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        await DataAsync(context, new
        {
            version = ApiVersion,
            fields = new object[]
            {
                new
                {
                    path = "runtime.sync_interval_seconds",
                    label = "同步间隔（秒）",
                    type = "number",
                    description = "后台差异同步的轮询间隔。",
                    min = 1,
                    max = 3600,
                    requires_restart = true,
                },
                new
                {
                    path = "runtime.outbox_retention_hours",
                    label = "事件保留（小时）",
                    type = "number",
                    description = "投递记录的保留窗口；过期事件会在事件页显示缺口。",
                    min = 1,
                    max = 168,
                    requires_restart = true,
                },
                new
                {
                    path = "runtime.profile_cache_ttl_minutes",
                    label = "资料缓存（分钟）",
                    type = "number",
                    min = 1,
                    max = 1440,
                    requires_restart = true,
                },
                new
                {
                    path = "runtime.auto_answer",
                    label = "自动接听",
                    type = "boolean",
                    description = "仅影响后续通话；真实媒体能力仍由 media.engine 决定。",
                    requires_restart = true,
                },
                new
                {
                    path = AccessTokenPath,
                    label = "Milky 业务访问令牌覆盖",
                    type = "secret",
                    description = "覆盖 config/local.json 中的 milky.access_token；清除后恢复配置文件值。修改后需要重启并同步更新前端保存的业务令牌。",
                    requires_restart = true,
                },
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleConfigGetAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        var snapshot = await _configs.LoadAsync(ct).ConfigureAwait(false);
        await DataAsync(context, ConfigDto(snapshot), ct).ConfigureAwait(false);
    }

    private async Task HandleConfigStatusAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        var snapshot = await _configs.LoadAsync(ct).ConfigureAwait(false);
        var requiresRestart = snapshot.Version != _options.AppliedConfigVersion;
        await DataAsync(context, new
        {
            state = requiresRestart ? "requires_restart" : "applied",
            saved_version = snapshot.Version.ToString(CultureInfo.InvariantCulture),
            applied_version = _options.AppliedConfigVersion.ToString(CultureInfo.InvariantCulture),
            message = requiresRestart
                ? "saved configuration is not active in this process; restart the adapter"
                : "saved configuration matches the running process",
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleConfigValidateAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "no active management session", ct).ConfigureAwait(false);
            return;
        }
        if (!await WriteAllowedAsync(context, session, ct).ConfigureAwait(false)) return;

        var body = await ReadAsync<ConfigSaveRequest>(context, ct).ConfigureAwait(false);
        if (body is null)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json",
                "request body must be a JSON object", ct).ConfigureAwait(false);
            return;
        }
        var parsed = ParseConfigRequest(body);
        await DataAsync(context, new
        {
            valid = parsed.Errors.Count == 0,
            errors = parsed.Errors,
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleConfigPutAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is null)
        {
            await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
                "no active management session", ct).ConfigureAwait(false);
            return;
        }
        if (!await WriteAllowedAsync(context, session, ct).ConfigureAwait(false)) return;

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            await ErrorAsync(context, StatusCodes.Status428PreconditionRequired, "precondition_required",
                "If-Match must carry the configuration version", ct).ConfigureAwait(false);
            return;
        }
        if (!long.TryParse(ifMatch.Trim().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_precondition",
                "If-Match is not a decimal configuration version", ct).ConfigureAwait(false);
            return;
        }

        var body = await ReadAsync<ConfigSaveRequest>(context, ct).ConfigureAwait(false);
        if (body is null)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json",
                "request body must be a JSON object", ct).ConfigureAwait(false);
            return;
        }
        var parsed = ParseConfigRequest(body);
        if (parsed.Errors.Count > 0 || parsed.Values is null)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "config_invalid",
                "configuration values failed validation", ct, parsed.Errors).ConfigureAwait(false);
            return;
        }

        try
        {
            var snapshot = await _configs.SaveAsync(
                version, parsed.Values, parsed.AccessToken, ct).ConfigureAwait(false);
            await DataAsync(context, ConfigDto(snapshot), ct).ConfigureAwait(false);
        }
        catch (AdminConfigConflictException ex)
        {
            await ErrorAsync(context, StatusCodes.Status412PreconditionFailed, "config_version_conflict",
                $"configuration changed; current version is {ex.CurrentVersion}; reload before saving", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "saving admin configuration failed");
            await ErrorAsync(context, StatusCodes.Status500InternalServerError, "config_save_failed",
                "configuration could not be saved; see adapter logs", ct).ConfigureAwait(false);
        }
    }

    private object ConfigDto(AdminConfigSnapshot snapshot) => new
    {
        version = snapshot.Version.ToString(CultureInfo.InvariantCulture),
        applied_version = _options.AppliedConfigVersion.ToString(CultureInfo.InvariantCulture),
        values = new
        {
            runtime = new
            {
                sync_interval_seconds = snapshot.SyncIntervalSeconds
                    ?? (int)Math.Round(_options.SyncInterval.TotalSeconds),
                outbox_retention_hours = snapshot.OutboxRetentionHours
                    ?? (int)Math.Round(_options.OutboxRetention.TotalHours),
                profile_cache_ttl_minutes = snapshot.ProfileCacheTtlMinutes
                    ?? (int)Math.Round(_options.ProfileCacheTtl.TotalMinutes),
                auto_answer = snapshot.AutoAnswer ?? _options.AutoAnswer,
            },
        },
        secrets = new Dictionary<string, object>
        {
            [AccessTokenPath] = new { is_set = snapshot.HasAccessTokenOverride },
        },
        requires_restart = snapshot.Version != _options.AppliedConfigVersion,
    };

    private static ParsedConfig ParseConfigRequest(ConfigSaveRequest body)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = body.Values?.Runtime;
        if (values is null)
        {
            errors["runtime"] = "缺少 runtime 配置";
            return new ParsedConfig(null, new AdminSecretUpdate(AdminSecretUpdateKind.Keep, null), errors);
        }

        void CheckRange(int? value, int min, int max, string path)
        {
            if (value is null)
                errors[path] = "必填";
            else if (value < min || value > max)
                errors[path] = $"必须在 {min} 到 {max} 之间";
        }

        CheckRange(values.SyncIntervalSeconds, 1, 3600, "runtime.sync_interval_seconds");
        CheckRange(values.OutboxRetentionHours, 1, 168, "runtime.outbox_retention_hours");
        CheckRange(values.ProfileCacheTtlMinutes, 1, 1440, "runtime.profile_cache_ttl_minutes");
        if (values.AutoAnswer is null)
            errors["runtime.auto_answer"] = "必填";

        var accessToken = new AdminSecretUpdate(AdminSecretUpdateKind.Keep, null);
        if (body.Secrets is { Count: > 0 })
        {
            foreach (var (path, secret) in body.Secrets)
            {
                if (path != AccessTokenPath)
                {
                    errors[path] = "未知的秘密字段";
                    continue;
                }
                var state = secret?.State?.ToLowerInvariant();
                switch (state)
                {
                    case "keep":
                    case null:
                    case "":
                        accessToken = new AdminSecretUpdate(AdminSecretUpdateKind.Keep, null);
                        break;
                    case "clear":
                        accessToken = new AdminSecretUpdate(AdminSecretUpdateKind.Clear, null);
                        break;
                    case "replace":
                        if (string.IsNullOrEmpty(secret!.Value) || secret.Value.Length < 16)
                            errors[AccessTokenPath] = "替换值至少需要 16 个字符";
                        else if (secret.Value.Length > 256)
                            errors[AccessTokenPath] = "替换值不能超过 256 个字符";
                        else
                            accessToken = new AdminSecretUpdate(AdminSecretUpdateKind.Replace, secret.Value);
                        break;
                    default:
                        errors[AccessTokenPath] = "更新方式必须是 keep、replace 或 clear";
                        break;
                }
            }
        }

        if (errors.Count > 0) return new ParsedConfig(null, accessToken, errors);
        return new ParsedConfig(
            new AdminConfigValues(
                values.SyncIntervalSeconds!.Value,
                values.OutboxRetentionHours!.Value,
                values.ProfileCacheTtlMinutes!.Value,
                values.AutoAnswer!.Value),
            accessToken,
            errors);
    }

    // ------------------------------------------------------------------ health

    private async Task HandleHealthAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        var databaseOk = await _database.HealthyAsync(ct).ConfigureAwait(false);
        var current = _runtime.Current;
        long pending = 0, expired = 0;
        DateTimeOffset? oldest = null;
        if (databaseOk && current is not null)
        {
            var metrics = await _outbox.GetMetricsAsync(current.Account.AccountId, ct).ConfigureAwait(false);
            pending = metrics.Pending;
            expired = metrics.Expired;
            oldest = metrics.OldestPendingAt;
        }
        var status = databaseOk ? (current is null ? "degraded" : "ok") : "unhealthy";
        context.Response.StatusCode = databaseOk
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        await DataAsync(context, new
        {
            status,
            storage = databaseOk ? "postgresql" : "unavailable",
            logged_in = current is not null,
            uin = current?.Account.Uin.ToString(CultureInfo.InvariantCulture),
            generation = current?.Generation.ToString(CultureInfo.InvariantCulture),
            instance = _runtime.InstanceId,
            media_engine = _calls.Capability.Engine,
            media_available = _calls.Capability.Available,
            event_subscribers = _events.Count,
            event_consumers = current is null ? Array.Empty<string>() : _events.Consumers(current.Account.AccountId),
            outbox_pending = pending,
            outbox_expired = expired,
            outbox_oldest_pending = oldest,
            management = new
            {
                configured = _auth.IsConfigured,
                sessions = "in-memory",
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleMetricsAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        var databaseOk = await _database.HealthyAsync(ct).ConfigureAwait(false);
        var current = _runtime.Current;
        OutboxMetrics? outbox = null;
        if (databaseOk && current is not null)
            outbox = await _outbox.GetMetricsAsync(current.Account.AccountId, ct).ConfigureAwait(false);
        var snapshot = await _configs.LoadAsync(ct).ConfigureAwait(false);
        var capability = _calls.Capability;
        await DataAsync(context, new
        {
            account = new
            {
                logged_in = current is not null,
                display_name = current?.Account.DisplayName,
                numeric_user_id = current?.Account.Uin.ToString(CultureInfo.InvariantCulture),
                generation = current?.Generation.ToString(CultureInfo.InvariantCulture),
            },
            backend = new
            {
                base_address = _options.BackendBaseAddress.ToString(),
                reachable = (bool?)null,
                reason = "not probed by the metrics endpoint",
            },
            storage = new
            {
                state = databaseOk ? "postgresql" : "unavailable",
                healthy = databaseOk,
            },
            outbox = outbox is null
                ? null
                : new
                {
                    pending = outbox.Pending,
                    expired = outbox.Expired,
                    delivered = outbox.Delivered,
                    oldest_pending_at = outbox.OldestPendingAt,
                },
            events = new
            {
                subscribers = _events.Count,
                consumers = current is null
                    ? Array.Empty<string>()
                    : _events.Consumers(current.Account.AccountId),
            },
            media = new
            {
                engine = capability.Engine,
                available = capability.Available,
                audio = capability.Audio,
                video = capability.Video,
                reason = capability.Reason,
            },
            config = new
            {
                applied_version = _options.AppliedConfigVersion.ToString(CultureInfo.InvariantCulture),
                saved_version = snapshot.Version.ToString(CultureInfo.InvariantCulture),
                requires_restart = snapshot.Version != _options.AppliedConfigVersion,
            },
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- logs

    private async Task HandleLogsAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        if (!TryParseLevel(context.Request.Query["level"], out var level))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_level",
                "level must be a LogLevel name", ct).ConfigureAwait(false);
            return;
        }
        var limit = 200;
        if (int.TryParse(context.Request.Query["limit"], out var requestedLimit))
            limit = Math.Clamp(requestedLimit, 1, 1000);

        var cursorText = context.Request.Query["cursor"].ToString();
        IReadOnlyList<AdminLogEntry> entries;
        long cursor = -1;
        if (long.TryParse(cursorText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCursor))
        {
            cursor = parsedCursor;
            entries = _logs.Read(cursor, level, limit);
        }
        else
        {
            entries = _logs.ReadLatest(level, limit);
            if (entries.Count > 0 && long.TryParse(entries[^1].Id, out var latest))
                cursor = latest;
        }

        await DataAsync(context, new
        {
            entries,
            next_cursor = cursor >= 0 ? cursor.ToString(CultureInfo.InvariantCulture) : null,
            dropped = _logs.Dropped,
            stream_id = _logs.StreamId,
            stream_started_at = _logs.StreamStartedAt,
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleLogsStreamAsync(HttpContext context, CancellationToken ct)
    {
        if (!await RequireSessionAsync(context, ct).ConfigureAwait(false)) return;
        if (!TryParseLevel(context.Request.Query["level"], out var level))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_level",
                "level must be a LogLevel name", ct).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        using var subscription = _logs.Subscribe(level);
        var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task WriteFrameAsync(string text, CancellationToken token)
        {
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await context.Response.WriteAsync(text, token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        var heartbeat = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync(heartbeatCts.Token).ConfigureAwait(false))
                    await WriteFrameAsync(": ping\n\n", heartbeatCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var entry in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var json = JsonSerializer.Serialize(entry, Json);
                await WriteFrameAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "management log stream ended");
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            writeLock.Dispose();
        }
    }

    private static bool TryParseLevel(string? value, out LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            level = LogLevel.Information;
            return true;
        }
        return Enum.TryParse(value, true, out level);
    }

    // ------------------------------------------------------------------ helpers

    private static int MapActionStatus(MilkyErrorCode code) => code switch
    {
        MilkyErrorCode.BadRequest => StatusCodes.Status400BadRequest,
        MilkyErrorCode.NotFound => StatusCodes.Status404NotFound,
        MilkyErrorCode.NotLoggedIn => StatusCodes.Status401Unauthorized,
        MilkyErrorCode.Conflict => StatusCodes.Status409Conflict,
        MilkyErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
        MilkyErrorCode.Unsupported => StatusCodes.Status501NotImplemented,
        MilkyErrorCode.Transient => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private async Task<bool> RequireSessionAsync(HttpContext context, CancellationToken ct)
    {
        var session = _auth.GetSession(context);
        if (session is not null)
        {
            AttachSession(context, session);
            return true;
        }
        await ErrorAsync(context, StatusCodes.Status401Unauthorized, "admin_session_required",
            "establish a management session first", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// An opaque session identity for clients that must distinguish "the same
    /// session refreshed its CSRF" from "a new login replaced the session".
    /// The cookie token and CSRF secret are never exposed here.
    /// </summary>
    private static void AttachSession(HttpContext context, AdminSession session)
        => context.Response.Headers["X-Nyarugram-Admin-Session"] = session.Id;

    private async Task<bool> WriteAllowedAsync(
        HttpContext context, AdminSession session, CancellationToken ct)
    {
        AttachSession(context, session);
        if (!_auth.OriginMatches(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "origin_rejected",
                "management writes require the same origin or a configured cors.allowed_origins entry", ct).ConfigureAwait(false);
            return false;
        }
        if (!_auth.CsrfMatches(context, session))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_rejected",
                "X-CSRF-Token is missing or does not match this management session", ct).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, CancellationToken ct)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (BadHttpRequestException)
        {
            return default;
        }
    }

    private static Task DataAsync(HttpContext context, object data, CancellationToken ct)
        => context.Response.WriteAsJsonAsync(new { data }, Json, ct);

    private static Task ErrorAsync(
        HttpContext context,
        int status,
        string code,
        string message,
        CancellationToken ct,
        Dictionary<string, string>? fields = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code,
                message,
                request_id = context.TraceIdentifier,
                fields,
            },
        }, Json, ct);
    }

    private sealed record LoginRequest(string? Username, string? Password, string? Secret);

    private sealed record AccountLoginRequest(string? Username, string? Password, string? TotpCode);

    private sealed record RuntimeValuesRequest(
        int? SyncIntervalSeconds,
        int? OutboxRetentionHours,
        int? ProfileCacheTtlMinutes,
        bool? AutoAnswer);

    private sealed record ConfigValuesRequest(RuntimeValuesRequest? Runtime);

    private sealed record SecretRequest(string? State, string? Value);

    private sealed record ConfigSaveRequest(
        ConfigValuesRequest? Values,
        Dictionary<string, SecretRequest?>? Secrets);

    private sealed record ParsedConfig(
        AdminConfigValues? Values,
        AdminSecretUpdate AccessToken,
        Dictionary<string, string> Errors);
}
