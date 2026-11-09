using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Nyarugram.Milky.Host;
using Nyarugram.Milky.Storage;
using Xunit;

namespace Nyarugram.Milky.Tests;

[Collection("adapter")]
public sealed class CorsTests
{
    private const string Origin = "http://192.168.2.10:3456";
    private readonly AdapterFixture _fixture;
    public CorsTests(AdapterFixture fixture) => _fixture = fixture;

    private HttpClient Client(string origin = Origin)
    {
        if (_fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {_fixture.StartupError.Message}");
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = _fixture.Client.BaseAddress,
        };
        client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    [SkippableTheory]
    [InlineData("/admin/v1/session", "POST", "content-type")]
    [InlineData("/admin/v1/config", "PUT", "content-type,x-csrf-token,if-match")]
    [InlineData("/admin/v1/session", "DELETE", "x-csrf-token")]
    [InlineData("/api/get_impl_info", "POST", "authorization,content-type")]
    [InlineData("/event", "GET", "authorization,last-event-id,x-nyarugram-consumer")]
    public async Task Allowed_preflight_succeeds(string path, string method, string headers)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains(method, string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        var allowed = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        foreach (var header in headers.Split(',')) Assert.Contains(header, allowed);
    }

    [SkippableFact]
    public async Task Unlisted_origin_has_no_cors_access_and_cannot_log_in()
    {
        using var client = Client("http://untrusted.invalid");
        using var request = new HttpRequestMessage(HttpMethod.Options, "/admin/v1/session");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        using var preflight = await client.SendAsync(request);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        using var login = await client.PostAsJsonAsync("/admin/v1/session", new { password = AdapterFixture.ManagementSecret });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
    }

    [SkippableFact]
    public async Task Cross_origin_session_keeps_cookie_csrf_and_session_identity()
    {
        using var client = Client();
        using var login = await client.PostAsJsonAsync("/admin/v1/session", new { password = AdapterFixture.ManagementSecret });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(Origin, login.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("samesite=strict", login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant());
        Assert.Contains("X-Nyarugram-Admin-Session", string.Join(",", login.Headers.GetValues("Access-Control-Expose-Headers")));
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        var csrf = data.GetProperty("csrf_token").GetString();
        using var session = await client.GetAsync("/admin/v1/session");
        Assert.Equal(data.GetProperty("session_id").GetString(), session.Headers.GetValues("X-Nyarugram-Admin-Session").Single());
        using var config = await client.GetAsync("/admin/v1/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        Assert.Contains("ETag", string.Join(",", config.Headers.GetValues("Access-Control-Expose-Headers")));
        using var configDocument = JsonDocument.Parse(await config.Content.ReadAsStringAsync());
        Assert.True(configDocument.RootElement.GetProperty("data").TryGetProperty("version", out _));
        using var logRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/logs/stream");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var logs = await client.SendAsync(logRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
            Assert.Equal(Origin, logs.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("text/event-stream", logs.Content.Headers.ContentType!.MediaType);
        }
        using var missingCsrf = await client.DeleteAsync("/admin/v1/session");
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        using var logout = await client.DeleteAsync("/admin/v1/session");
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
    }

    [SkippableFact]
    public async Task Business_and_sse_responses_expose_cors_headers_and_keep_authentication()
    {
        using var client = Client();
        using var unauthenticated = await client.PostAsJsonAsync("/api/get_impl_info", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(Origin, unauthenticated.Headers.GetValues("Access-Control-Allow-Origin").Single());
        client.DefaultRequestHeaders.Authorization = new("Bearer", _fixture.AccessToken);
        using var business = await client.PostAsJsonAsync("/api/get_impl_info", new { });
        Assert.Equal(HttpStatusCode.OK, business.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/event?nyarugram_consumer=cors-test-sse");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(Origin, stream.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Websocket_checks_origin(bool allowed)
    {
        using var client = Client();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", allowed ? Origin : "http://untrusted.invalid");
        var url = new UriBuilder(_fixture.Client.BaseAddress!)
        {
            Scheme = "ws", Path = "/event", Query = "access_token=" + Uri.EscapeDataString(_fixture.AccessToken),
        }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (allowed)
        {
            await socket.ConnectAsync(url, timeout.Token);
            Assert.Equal(WebSocketState.Open, socket.State);
            socket.Abort();
        }
        else
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(url, timeout.Token));
    }
}

public sealed class CorsConfigurationTests
{
    private static MilkyOptions Load(string? origin, string sameSite = "Strict", string secure = "false")
    {
        var values = new Dictionary<string, string?>
        {
            ["backend:base_address"] = "https://backend.invalid",
            ["backend:server_domain"] = "backend.invalid",
            ["storage:connection_string"] = "Host=127.0.0.1;Database=unused",
            ["storage:secret_key"] = Convert.ToBase64String(new byte[32]),
            ["milky:access_token"] = "test-access-token-0123456789",
            ["admin:secret"] = "test-management-secret-0123456789",
            ["cors:allowed_origins:0"] = origin,
            ["admin:cookie_same_site"] = sameSite,
            ["admin:secure_cookie"] = secure,
        };
        if (origin is null) values.Remove("cors:allowed_origins:0");
        return MilkyOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void Default_configuration_keeps_same_origin_and_non_browser_clients_working()
    {
        var options = Load(null);
        Assert.Empty(options.AllowedOrigins);
        Assert.Equal(SameSiteMode.Strict, options.ManagementCookieSameSite);
        var auth = new AdminAuthService(options);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 8091);
        Assert.True(auth.OriginMatches(context));
        context.Request.Headers.Origin = "http://127.0.0.1:8091";
        Assert.True(auth.OriginMatches(context));
        context.Request.Headers.Origin = "http://127.0.0.1:3456";
        Assert.False(auth.OriginMatches(context));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://*.example")]
    [InlineData("null")]
    [InlineData("https://webui.invalid/path")]
    [InlineData("https://webui.invalid?query=1")]
    [InlineData("https://user:password@webui.invalid")]
    public void Invalid_origins_are_rejected(string origin)
        => Assert.Throws<InvalidOperationException>(() => Load(origin));

    [Fact]
    public void Cross_site_cookie_requires_secure_and_settings_survive_admin_config_overlay()
    {
        Assert.Throws<InvalidOperationException>(() => Load("https://webui.invalid", "None"));
        Assert.Throws<InvalidOperationException>(() => Load("https://webui.invalid", "invalid"));
        var options = Load("https://webui.invalid:443/", "None", "true").WithAdminConfig(AdminConfigSnapshot.Empty);
        Assert.Equal(["https://webui.invalid"], options.AllowedOrigins);
        var auth = new AdminAuthService(options);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        Assert.Equal(AdminLoginStatus.Ok, auth.Login(context, "test-management-secret-0123456789").Status);
        var cookie = context.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.Contains("samesite=none", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        var logout = new DefaultHttpContext();
        logout.Request.Scheme = "https";
        auth.Logout(logout);
        Assert.Contains("samesite=none", logout.Response.Headers.SetCookie.ToString().ToLowerInvariant());
    }
}
