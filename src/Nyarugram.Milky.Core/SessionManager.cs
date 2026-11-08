using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Storage;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Owns the interactive login lifecycle: password + authenticator, encrypted session
/// persistence, startup restore and logout. Passwords are never stored.
/// </summary>
public sealed class SessionManager
{
    private readonly BackendClient _client;
    private readonly AccountStore _accounts;
    private readonly AccountApi _accountApi;
    private readonly MilkyRuntime _runtime;
    private readonly ProfileCache _cache;
    private readonly EventBus _events;
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(
        BackendClient client, AccountStore accounts, AccountApi accountApi, MilkyRuntime runtime,
        ProfileCache cache, EventBus events, ILogger<SessionManager> logger)
    {
        _events = events;
        _client = client;
        _accounts = accounts;
        _accountApi = accountApi;
        _runtime = runtime;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SessionContext> LoginAsync(
        string username, string password, string? totpCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "username and password are required");

        var session = await _client.LoginAsync(username, password, totpCode, "login" + Guid.NewGuid().ToString("N"), ct)
            .ConfigureAwait(false);
        if (session.User is null || session.Token.Length == 0)
            throw new MilkyActionException(MilkyErrorCode.Internal, "backend returned an empty session");

        var account = await _accounts.UpsertAccountAsync(
            _client.ServerDomain, session.User.UserId, session.User.TlUserId,
            session.User.Username, session.User.DisplayName, ct).ConfigureAwait(false);

        var stored = await _accounts.ReplaceSessionAsync(
            account.AccountId, session.Token, DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAtMs), ct)
            .ConfigureAwait(false);

        // A new generation starts with an empty cache scope.
        InvalidatePreviousScope();

        var context = new SessionContext(account, stored.Generation, stored.Token);
        _runtime.Bind(context);
        _logger.LogInformation("logged in as {Username} (uin {Uin})", account.Username, account.Uin);
        return context;
    }

    /// <summary>
    /// Re-binds a stored session at startup. The account must belong to the configured
    /// server identity; a stored account from another deployment is never restored.
    /// Any 401 invalidates the token so the adapter does not loop on rejected credentials.
    /// </summary>
    public async Task<SessionContext?> RestoreAsync(CancellationToken ct)
    {
        var accounts = await _accounts.ListAccountsAsync(_client.ServerDomain, ct).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            // Surface a configuration mismatch instead of silently binding an unrelated account.
            var other = await _accounts.GetSingleAccountAsync(ct).ConfigureAwait(false);
            if (other is not null)
                _logger.LogError(
                    "stored account belongs to server {Stored} but backend.server_domain is {Configured}; refusing to restore",
                    other.ServerDomain, _client.ServerDomain);
            return null;
        }
        if (accounts.Count > 1)
            _logger.LogWarning("{Count} accounts exist for {Domain}; restoring the oldest", accounts.Count, _client.ServerDomain);

        var account = accounts[0];
        var session = await _accounts.GetActiveSessionAsync(account.AccountId, ct).ConfigureAwait(false);
        if (session is null) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("stored session expired at {Expiry}; login required", session.ExpiresAt);
            await _accounts.RevokeSessionsAsync(account.AccountId, ct).ConfigureAwait(false);
            return null;
        }

        try
        {
            var me = await _client.AccountMeAsync(session.Token, "me" + Guid.NewGuid().ToString("N"), ct)
                .ConfigureAwait(false);
            if (me.TlUserId != account.Uin)
            {
                // The numeric identity is the Milky uin; a changed TL id must not silently
                // re-point every stored reference.
                _logger.LogError("backend account uin changed from {Stored} to {Current}; refusing to bind",
                    account.Uin, me.TlUserId);
                return null;
            }
        }
        catch (BackendException ex) when (ex.HttpStatus is 401 or 403)
        {
            _logger.LogWarning("stored session rejected by backend; revoking");
            await _accounts.RevokeSessionsAsync(account.AccountId, ct).ConfigureAwait(false);
            return null;
        }
        catch (BackendException ex)
        {
            // Backend unreachable at startup: keep the token, report degraded, retry later.
            _logger.LogWarning("cannot validate stored session yet: {Code}", ex.Code);
        }

        var context = new SessionContext(account, session.Generation, session.Token);
        _runtime.Bind(context);
        return context;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var context = _runtime.Current;
        if (context is null) return;
        try
        {
            await _accountApi.LogoutAsync(context, ct).ConfigureAwait(false);
        }
        catch (BackendException ex)
        {
            // Local logout must succeed even when the backend is unreachable, otherwise a
            // dead session would pin the account forever.
            _logger.LogWarning("backend logout failed: {Code}", ex.Code);
        }
        await _accounts.RevokeSessionsAsync(context.Account.AccountId, ct).ConfigureAwait(false);
        _cache.InvalidateScope(context.Scope);
        _events.CancelScope(context.Scope);
        _runtime.Clear();
    }

    private void InvalidatePreviousScope()
    {
        if (_runtime.Current is not { } previous) return;
        _cache.InvalidateScope(previous.Scope);
        // Event subscriptions belong to the old session and must not receive the new one's events.
        _events.CancelScope(previous.Scope);
    }
}
