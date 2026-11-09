using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nyarugram.Milky.Backend;
using Nyarugram.Milky.Host;
using Xunit;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

public sealed class LargeClientHelloTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Configuration_is_optional(string? value, bool expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["backend:base_address"] = "https://localhost:8521",
            ["backend:server_domain"] = "example.local",
            ["storage:connection_string"] = "Host=localhost;Database=isolated",
            ["storage:secret_key"] = Convert.ToBase64String(new byte[32]),
            ["milky:access_token"] = "isolated-test-access-token",
        };
        if (value is not null) values["backend:large_clienthello"] = value;
        var options = MilkyOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(expected, options.LargeClientHello);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Real_http_and_websocket_clienthello_obey_setting(bool large, bool websocket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}");
        using var client = Client(endpoint, large);
        var operation = Record.ExceptionAsync(async () =>
        {
            if (websocket)
            {
                using var socket = await client.ConnectEventStreamAsync("test-bearer", timeout.Token);
            }
            else await client.GetAuthCapabilitiesAsync("hello-probe", timeout.Token);
        });
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, timeout.Token);
        Assert.Equal(22, header[0]);
        var recordBytes = 5 + (header[3] << 8) + header[4];
        var body = new byte[recordBytes - 5];
        await stream.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal(1, body[0]);
        Assert.Equal(large, recordBytes >= 1500);
        peer.Close();
        Assert.NotNull(await operation);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("mismatch")]
    [InlineData("system")]
    public async Task Padded_transport_completes_http_and_wss_and_enforces_certificate(string pinMode)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapPost("/api/v1/auth", async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            var rpc = P.RpcRequest.Parser.ParseFrom(body.ToArray());
            context.Response.ContentType = BackendClient.ProtobufMediaType;
            var response = new P.RpcResponse
            {
                ProtocolMajor = 1,
                RequestId = rpc.RequestId,
                AuthCapabilities = new P.AuthCapabilities { SelectedProtocol = "protobuf-v1" },
            };
            await context.Response.Body.WriteAsync(response.ToByteArray());
        });
        app.Map("/api/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(BackendClient.EventSubProtocol);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
        });
        await app.StartAsync();
        var pin = pinMode switch
        {
            "match" => Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            "mismatch" => new string('0', 64),
            _ => null,
        };
        using var client = Client(new Uri(app.Urls.Single()), true, pin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (pinMode == "match")
        {
            Assert.Equal("protobuf-v1", (await client.GetAuthCapabilitiesAsync("tls-test", timeout.Token)).SelectedProtocol);
            using var socket = await client.ConnectEventStreamAsync("test-bearer", timeout.Token);
            Assert.Equal(BackendClient.EventSubProtocol, socket.SubProtocol);
        }
        else
        {
            await Assert.ThrowsAsync<BackendException>(() => client.GetAuthCapabilitiesAsync("tls-test", timeout.Token));
            await Assert.ThrowsAsync<BackendException>(() => client.ConnectEventStreamAsync("test-bearer", timeout.Token));
        }
        await app.StopAsync(timeout.Token);
    }

    private static BackendClient Client(Uri endpoint, bool large, string? pin = null) => new(new BackendOptions
    {
        BaseAddress = endpoint,
        ServerDomain = "example.local",
        LargeClientHello = large,
        CertificateSha256Pin = pin,
    }, NullLogger<BackendClient>.Instance);
}