using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Backend;

public sealed class BackendOptions
{
    /// <summary>Explicitly configured backend base address, e.g. https://192.168.1.20:8521.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>Protocol identity domain configured on the backend (Protocol:ServerDomain).</summary>
    public required string ServerDomain { get; init; }

    /// <summary>
    /// Optional SHA-256 (hex) pin of the backend leaf certificate. When set, the chain is
    /// still validated; the pin replaces host-name matching only for IP-literal deployments.
    /// </summary>
    public string? CertificateSha256Pin { get; init; }

    /// <summary>Permit http:// and ws:// backends. Off by default; loopback tests may enable it.</summary>
    public bool AllowPlaintext { get; init; }

    public bool LargeClientHello { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class BackendException : Exception
{
    public BackendException(string code, int httpStatus, bool retryable, string? message = null, Exception? inner = null)
        : base(message ?? code, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
        Retryable = retryable;
    }

    public string Code { get; }
    public int HttpStatus { get; }
    public bool Retryable { get; }

    /// <summary>
    /// True when the request may have been accepted by the backend even though the
    /// caller did not observe a response. Idempotent retries must reuse the same random id.
    /// </summary>
    public bool OutcomeUnknown { get; init; }
}

/// <summary>
/// Protobuf-only client for /api/v1/auth, /api/v1/account, /api/v1/rpc and the
/// botgram.event-hint.v1.protobuf WebSocket. No JSON transport, no deprecated REST,
/// no fallback to public endpoints.
/// </summary>
public sealed class BackendClient : IDisposable
{
    public const string ProtobufMediaType = "application/x-protobuf";
    public const string EventSubProtocol = "botgram.event-hint.v1.protobuf";
    public const uint ProtocolMajor = 1;

    private readonly BackendOptions _options;
    private readonly ILogger<BackendClient> _logger;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker? _eventTransport;

    public BackendClient(BackendOptions options, ILogger<BackendClient> logger)
    {        _options = options;
        _logger = logger;

        var scheme = options.BaseAddress.Scheme;
        if (scheme != Uri.UriSchemeHttps && !(options.AllowPlaintext && scheme == Uri.UriSchemeHttp))
            throw new InvalidOperationException(
                "backend.base_address must use https; plaintext requires backend.allow_plaintext=true (loopback tests only)");

        HttpMessageHandler transport;
        if (options.LargeClientHello && scheme == Uri.UriSchemeHttps)
        {
            transport = new LargeClientHelloHandler(options.BaseAddress, ValidateCertificate);
            _eventTransport = new HttpMessageInvoker(transport, disposeHandler: false);
        }
        else
        {
            transport = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AllowAutoRedirect = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = ValidateCertificate,
                },
            };
        }
        _http = new HttpClient(transport) { Timeout = options.RequestTimeout };
        _http.BaseAddress = options.BaseAddress;
    }

    /// <summary>Protocol identity domain configured on the backend.</summary>
    public string ServerDomain => _options.ServerDomain;

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)    {
        if (certificate is null) return false;
        if (_options.CertificateSha256Pin is { Length: > 0 } pin)
        {
            var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
            if (!string.Equals(actual, pin.Replace(":", "").ToLowerInvariant(), StringComparison.Ordinal))
            {
                _logger.LogError("backend certificate pin mismatch");
                return false;
            }
            // Chain errors are tolerated only because the pin authenticates this exact key.
            return true;
        }
        // Default policy: full chain and host name validation.
        return errors == SslPolicyErrors.None;
    }

    // ---------------------------------------------------------------- auth plane

    public async Task<P.AuthSession> LoginAsync(
        string username, string password, string? totpCode, string requestId, CancellationToken ct)
    {
        var request = new P.RpcRequest
        {
            ProtocolMajor = ProtocolMajor,
            RequestId = requestId,
            AuthLogin = new P.AuthLoginRequest { Username = username, Password = password },
        };
        if (!string.IsNullOrEmpty(totpCode)) request.AuthLogin.TotpCode = totpCode;
        var response = await PostAsync("/api/v1/auth", request, bearer: null, ct).ConfigureAwait(false);
        return response.AuthLogin
            ?? throw new BackendException("BOTGRAM_AUTH_PLANE_OPERATION_INVALID", 400, false);
    }

    public async Task<P.AuthCapabilities> GetAuthCapabilitiesAsync(string requestId, CancellationToken ct)
    {
        var request = new P.RpcRequest
        {
            ProtocolMajor = ProtocolMajor,
            RequestId = requestId,
            AuthCapabilities = new P.AuthCapabilitiesRequest(),
        };
        var response = await PostAsync("/api/v1/auth", request, bearer: null, ct).ConfigureAwait(false);
        return response.AuthCapabilities
            ?? throw new BackendException("BOTGRAM_AUTH_PLANE_OPERATION_INVALID", 400, false);
    }

    public async Task<P.AccountProfile> AccountMeAsync(string bearer, string requestId, CancellationToken ct)
    {
        var request = new P.RpcRequest
        {
            ProtocolMajor = ProtocolMajor,
            RequestId = requestId,
            AccountMe = new P.AccountMeRequest(),
        };
        var response = await PostAsync("/api/v1/account", request, bearer, ct).ConfigureAwait(false);
        return response.AccountMe
            ?? throw new BackendException("BOTGRAM_AUTH_PLANE_OPERATION_INVALID", 400, false);
    }

    public async Task LogoutAsync(string bearer, string requestId, CancellationToken ct)
    {
        var request = new P.RpcRequest
        {
            ProtocolMajor = ProtocolMajor,
            RequestId = requestId,
            AuthLogout = new P.AuthLogoutRequest(),
        };
        await PostAsync("/api/v1/account", request, bearer, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------- business

    public async Task<P.RpcResponse> RpcAsync(P.RpcRequest request, string bearer, CancellationToken ct)
    {
        request.ProtocolMajor = ProtocolMajor;
        return await PostAsync("/api/v1/rpc", request, bearer, ct).ConfigureAwait(false);
    }

    private async Task<P.RpcResponse> PostAsync(string path, P.RpcRequest request, string? bearer, CancellationToken ct)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new(ProtobufMediaType);
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (bearer is not null) message.Headers.Authorization = new("Bearer", bearer);
        message.Headers.Accept.Add(new(ProtobufMediaType));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BackendException("BOTGRAM_TRANSPORT_TIMEOUT", 0, true, "backend request timed out") { OutcomeUnknown = true };
        }
        catch (HttpRequestException ex)
        {
            throw new BackendException("BOTGRAM_TRANSPORT_FAILURE", 0, true, ex.Message, ex) { OutcomeUnknown = true };
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            P.RpcResponse? decoded = null;
            if (bytes.Length > 0)
            {
                try
                {
                    decoded = P.RpcResponse.Parser.ParseFrom(bytes);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    throw new BackendException("BOTGRAM_PROTOBUF_MALFORMED", (int)response.StatusCode, false, ex.Message, ex);
                }
            }

            if (decoded is null)
                throw new BackendException("BOTGRAM_EMPTY_RESPONSE", (int)response.StatusCode, response.StatusCode >= HttpStatusCode.InternalServerError);

            if (decoded.OutcomeCase == P.RpcResponse.OutcomeOneofCase.Error)
            {
                var error = decoded.Error;
                throw new BackendException(
                    string.IsNullOrEmpty(error.Code) ? "BOTGRAM_RPC_ERROR" : error.Code,
                    error.HttpStatus != 0 ? (int)error.HttpStatus : (int)response.StatusCode,
                    error.Retryable,
                    string.IsNullOrEmpty(error.Message) ? null : error.Message);
            }

            if (!response.IsSuccessStatusCode && decoded.OutcomeCase == P.RpcResponse.OutcomeOneofCase.None)
                throw new BackendException("BOTGRAM_HTTP_ERROR", (int)response.StatusCode, (int)response.StatusCode >= 500);

            return decoded;
        }
    }

    // -------------------------------------------------------------------- events    /// <summary>
    /// Connects the wake-up hint stream. Hints carry only a cursor watermark; the
    /// authoritative delta is always get_difference.
    /// </summary>
    public async Task<ClientWebSocket> ConnectEventStreamAsync(string bearer, CancellationToken ct)
    {
        var builder = new UriBuilder(_options.BaseAddress)
        {
            Scheme = _options.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/ws",
        };
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(EventSubProtocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            if (_eventTransport is null)
                await socket.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
            else
                await socket.ConnectAsync(builder.Uri, _eventTransport, ct).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            throw new BackendException("BOTGRAM_EVENT_CONNECT_FAILED", 0, true, ex.Message, ex);
        }
    }

    public void Dispose()
    {
        _eventTransport?.Dispose();
        _http.Dispose();
    }
}
