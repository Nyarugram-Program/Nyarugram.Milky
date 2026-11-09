using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Nyarugram.Milky.Backend;

/// <summary>HTTP/1.1 and WebSocket over a verified TLS stream with padded ALPN offers.</summary>
internal sealed class LargeClientHelloHandler : HttpMessageHandler
{
    private readonly Uri _endpoint;
    private readonly RemoteCertificateValidationCallback _validateCertificate;
    private readonly HttpMessageInvoker _transport;

    public LargeClientHelloHandler(Uri endpoint, RemoteCertificateValidationCallback validateCertificate)
    {
        _endpoint = endpoint;
        _validateCertificate = validateCertificate;
        _transport = new HttpMessageInvoker(new SocketsHttpHandler
        {
            // This transport establishes TLS directly to the configured backend.
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync,
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var original = request.RequestUri ?? throw new InvalidOperationException("Missing backend URI");
        if (original.Scheme is not ("https" or "wss")
            || !string.Equals(original.IdnHost, _endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || original.Port != _endpoint.Port)
            throw new InvalidOperationException("Large ClientHello transport requires the configured HTTPS origin");

        // SocketsHttpHandler replaces custom ALPN offers on HTTPS connections. Supply
        // an already authenticated TLS stream to its HTTP/1.1 framing layer instead.
        // The peer and port remain fixed, and no cleartext connection is created.
        request.RequestUri = new UriBuilder(original) { Scheme = Uri.UriSchemeHttp, Port = original.Port }.Uri;
        request.Headers.Host ??= original.Authority;
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        try
        {
            return await _transport.SendAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            request.RequestUri = original;
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (!string.Equals(context.DnsEndPoint.Host, _endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != _endpoint.Port)
            throw new InvalidOperationException("Unexpected backend connection target");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        SslStream? tls = null;
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false, _validateCertificate);
            var protocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };
            // These legal, unknown offers alone encode over 1500 bytes. Keep every
            // name below 128 bytes, including for peers with signed ALPN decoders.
            for (var i = 0; i < 12; i++)
                protocols.Add(new SslApplicationProtocol(Encoding.ASCII.GetBytes(
                    $"milky-padding-{i:D2}-".PadRight(127, 'x'))));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _endpoint.IdnHost,
                ApplicationProtocols = protocols,
            }, ct).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            tls?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _transport.Dispose();
        base.Dispose(disposing);
    }
}