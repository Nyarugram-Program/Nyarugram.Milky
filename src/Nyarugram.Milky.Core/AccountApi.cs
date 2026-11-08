using Nyarugram.Milky.Backend;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

/// <summary>
/// Account-plane calls live on <c>/api/v1/account</c>, not on the business RPC endpoint.
/// Every entry point re-checks the captured generation before and after the round trip so
/// a response that arrives after logout or re-login is never delivered.
/// </summary>
public sealed class AccountApi
{
    private readonly BackendClient _client;
    private readonly MilkyRuntime _runtime;

    public AccountApi(BackendClient client, MilkyRuntime runtime)
    {
        _client = client;
        _runtime = runtime;
    }

    public async Task<P.AccountProfile> MeAsync(SessionContext context, CancellationToken ct)
    {
        _runtime.RequireCurrent(context);
        var profile = await _client
            .AccountMeAsync(context.BearerToken, BackendRpc.NewRequestId(), ct)
            .ConfigureAwait(false);
        _runtime.RequireCurrent(context);
        return profile;
    }

    public async Task LogoutAsync(SessionContext context, CancellationToken ct)
    {
        _runtime.RequireCurrent(context);
        await _client.LogoutAsync(context.BearerToken, BackendRpc.NewRequestId(), ct).ConfigureAwait(false);
        _runtime.RequireCurrent(context);
    }
}
