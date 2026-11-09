using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Nyarugram.Milky.Host;

public sealed record AdminSession(string Id, string CsrfToken, DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory Web management sessions. These sessions are intentionally independent from
/// both the Milky access token and the backend bearer token.
/// </summary>
public sealed class AdminAuthService
{
    public const string CookieName = "nyarugram_admin";
    public const string CsrfHeader = "X-CSRF-Token";

    private sealed record StoredSession(AdminSession Session, byte[] TokenHash);
    private sealed record Failures(int Count, DateTimeOffset WindowStarted);

    private readonly MilkyOptions _options;
    private readonly ConcurrentDictionary<string, StoredSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private readonly object _failureGate = new();

    public AdminAuthService(MilkyOptions options) => _options = options;

    public bool IsConfigured => !string.IsNullOrEmpty(_options.ManagementSecret);

    public AdminLoginResult Login(HttpContext context, string? secret)
    {
        if (!IsConfigured) return AdminLoginResult.NotConfigured;
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (IsRateLimited(client)) return AdminLoginResult.RateLimited;
        if (secret is null || !SecretEquals(secret, _options.ManagementSecret!))
        {
            RecordFailure(client);
            return AdminLoginResult.Unauthorized;
        }

        lock (_failureGate) _failures.Remove(client);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new AdminSession(
            Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            DateTimeOffset.UtcNow.Add(_options.ManagementSessionLifetime));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _sessions[session.Id] = new StoredSession(session, hash);

        if (context.Request.Cookies.TryGetValue(CookieName, out var previous))
            Revoke(previous);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = _options.ManagementCookieSameSite,
            Secure = _options.ManagementSecureCookie || context.Request.IsHttps,
            Path = "/admin/v1",
            Expires = session.ExpiresAt,
        });
        return AdminLoginResult.Success(session);
    }

    public AdminSession? GetSession(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || token.Length == 0)
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var pair in _sessions)
        {
            if (!CryptographicOperations.FixedTimeEquals(pair.Value.TokenHash, hash)) continue;
            if (pair.Value.Session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _sessions.TryRemove(pair.Key, out _);
                return null;
            }
            return pair.Value.Session;
        }
        return null;
    }

    public void Logout(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token)) Revoke(token);
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = _options.ManagementCookieSameSite,
            Secure = _options.ManagementSecureCookie || context.Request.IsHttps,
            Path = "/admin/v1",
        });
    }

    public bool CsrfMatches(HttpContext context, AdminSession session)
    {
        var supplied = context.Request.Headers[CsrfHeader].ToString();
        if (supplied.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(session.CsrfToken)));
    }

    /// <summary>Accept same-origin and configured WebUI origins. Non-browser clients may omit Origin.</summary>
    public bool OriginMatches(HttpContext context)
    {
        var value = context.Request.Headers.Origin.ToString();
        if (value.Length == 0) return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)) return false;
        if (origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            return false;
        if (_options.AllowedOrigins.Contains(origin.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
            return true;
        var request = context.Request;
        var expectedPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        var actualPort = origin.IsDefaultPort ? (origin.Scheme == "https" ? 443 : 80) : origin.Port;
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && actualPort == expectedPort;
    }

    private void Revoke(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var pair in _sessions)
            if (CryptographicOperations.FixedTimeEquals(pair.Value.TokenHash, hash))
                _sessions.TryRemove(pair.Key, out _);
    }

    private bool IsRateLimited(string client)
    {
        lock (_failureGate)
        {
            CleanupFailures();
            return _failures.TryGetValue(client, out var failures)
                && failures.Count >= 5
                && DateTimeOffset.UtcNow - failures.WindowStarted < TimeSpan.FromMinutes(1);
        }
    }

    private void RecordFailure(string client)
    {
        lock (_failureGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_failures.TryGetValue(client, out var previous)
                && now - previous.WindowStarted < TimeSpan.FromMinutes(1))
            {
                _failures[client] = previous with { Count = previous.Count + 1 };
            }
            else
            {
                if (_failures.Count >= 1024) _failures.Clear();
                _failures[client] = new Failures(1, now);
            }
        }
    }

    private void CleanupFailures()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var pair in _failures)
            if (pair.Value.WindowStarted < cutoff) _failures.Remove(pair.Key);
    }

    private static bool SecretEquals(string supplied, string expected)
        => CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

public sealed record AdminLoginResult(AdminLoginStatus Status, AdminSession? Session = null)
{
    public static AdminLoginResult Success(AdminSession session) => new(AdminLoginStatus.Ok, session);
    public static readonly AdminLoginResult Unauthorized = new(AdminLoginStatus.Unauthorized);
    public static readonly AdminLoginResult NotConfigured = new(AdminLoginStatus.NotConfigured);
    public static readonly AdminLoginResult RateLimited = new(AdminLoginStatus.RateLimited);
}

public enum AdminLoginStatus
{
    Ok,
    Unauthorized,
    NotConfigured,
    RateLimited,
}
