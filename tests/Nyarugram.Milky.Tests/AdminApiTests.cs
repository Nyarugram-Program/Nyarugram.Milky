using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Host;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// Contract tests for the Web management surface. They use the real host, real
/// PostgreSQL schema and the fake Protobuf backend; no management API stand-in.
/// </summary>
[Collection("adapter")]
public sealed class AdminApiTests
{
    private const string AccessTokenPath = "milky.access_token";

    private readonly AdapterFixture _fixture;

    public AdminApiTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    private HttpClient NewClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
    })
    {
        BaseAddress = _fixture.Client.BaseAddress,
    };

    private async Task<(HttpClient Client, string Csrf)> LoginAsync(bool withCsrf = true)
    {
        var client = NewClient();
        using var response = await client.PostAsJsonAsync("/admin/v1/session", new
        {
            username = "default",
            password = AdapterFixture.ManagementSecret,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var csrf = document.RootElement.GetProperty("data").GetProperty("csrf_token").GetString();
        Assert.False(string.IsNullOrEmpty(csrf));
        if (withCsrf)
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-Token", csrf);
        return (client, csrf!);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static object ConfigPayload(
        int syncInterval = 7,
        bool autoAnswer = true,
        string secretState = "keep",
        string? secretValue = null)
    {
        object secret = secretValue is null
            ? new { state = secretState }
            : new { state = secretState, value = secretValue };
        return new
        {
            values = new
            {
                runtime = new
                {
                    sync_interval_seconds = syncInterval,
                    outbox_retention_hours = 6,
                    profile_cache_ttl_minutes = 10,
                    auto_answer = autoAnswer,
                },
            },
            secrets = new Dictionary<string, object> { [AccessTokenPath] = secret },
        };
    }

    private static async Task<HttpResponseMessage> PutConfigAsync(
        HttpClient client, long version, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/v1/config")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", version.ToString());
        return await client.SendAsync(request);
    }

    [SkippableFact]
    public async Task Info_is_public_and_declares_real_capabilities()
    {
        RequireStorage(_fixture);
        using var client = NewClient();
        using var response = await client.GetAsync("/admin/v1/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await ReadDataAsync(response);
        Assert.Equal("nyarugram-milky", data.GetProperty("adapter_kind").GetString());
        var capabilities = data.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("config").GetProperty("supported").GetBoolean());
        Assert.True(capabilities.GetProperty("logs").GetProperty("supported").GetBoolean());
        Assert.True(capabilities.GetProperty("account").GetProperty("supported").GetBoolean());
        Assert.Equal(
            "loopback",
            capabilities.GetProperty("media").GetProperty("engine").GetString());
    }

    [SkippableFact]
    public async Task Session_rejects_wrong_secret_and_accepts_the_configured_one()
    {
        RequireStorage(_fixture);
        using var client = NewClient();
        using (var wrong = await client.PostAsJsonAsync("/admin/v1/session", new
        {
            username = "default",
            password = "wrong-management-secret",
        }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var login = await client.PostAsJsonAsync("/admin/v1/session", new
        {
            username = "default",
            password = AdapterFixture.ManagementSecret,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginData = await ReadDataAsync(login);
        Assert.True(loginData.GetProperty("authenticated").GetBoolean());
        Assert.False(string.IsNullOrEmpty(loginData.GetProperty("csrf_token").GetString()));
        var sessionId = loginData.GetProperty("session_id").GetString();
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.Equal(sessionId, login.Headers.GetValues("X-Nyarugram-Admin-Session").Single());

        using var session = await client.GetAsync("/admin/v1/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var sessionData = await ReadDataAsync(session);
        Assert.True(sessionData.GetProperty("authenticated").GetBoolean());
        Assert.Equal(sessionId, sessionData.GetProperty("session_id").GetString());
        Assert.Equal(sessionId, session.Headers.GetValues("X-Nyarugram-Admin-Session").Single());

        using var account = await client.GetAsync("/admin/v1/account");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal(sessionId, account.Headers.GetValues("X-Nyarugram-Admin-Session").Single());
    }

    [SkippableFact]
    public async Task Session_cookie_is_required_for_management_reads()
    {
        RequireStorage(_fixture);
        using var client = NewClient();
        using var response = await client.GetAsync("/admin/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var account = await client.GetAsync("/admin/v1/account");
        Assert.Equal(HttpStatusCode.Unauthorized, account.StatusCode);
    }

    [SkippableFact]
    public async Task Account_reports_the_real_runtime_binding()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            using var response = await client.GetAsync("/admin/v1/account");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await ReadDataAsync(response);
            Assert.Equal("active", data.GetProperty("state").GetString());
            Assert.Equal(
                FakeBackend.AccountUin.ToString(),
                data.GetProperty("numeric_user_id").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("generation").GetString()));
        }
    }

    [SkippableFact]
    public async Task Config_writes_require_csrf_and_if_match()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync(withCsrf: false);
        using (client)
        {
            using var current = await client.GetAsync("/admin/v1/config");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            var version = long.Parse(
                (await ReadDataAsync(current)).GetProperty("version").GetString()!);

            using var noCsrf = await PutConfigAsync(client, version, ConfigPayload());
            Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);

            using var session = await client.GetAsync("/admin/v1/session");
            var csrf = (await ReadDataAsync(session)).GetProperty("csrf_token").GetString()!;
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-Token", csrf);

            using var noIfMatch = await client.PutAsJsonAsync(
                "/admin/v1/config", ConfigPayload());
            Assert.Equal(HttpStatusCode.PreconditionRequired, noIfMatch.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Config_validate_reports_field_errors()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            using var response = await client.PostAsJsonAsync(
                "/admin/v1/config/validate", ConfigPayload(syncInterval: 0));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await ReadDataAsync(response);
            Assert.False(data.GetProperty("valid").GetBoolean());
            Assert.True(data.GetProperty("errors").TryGetProperty(
                "runtime.sync_interval_seconds", out _));
        }
    }

    [SkippableFact]
    public async Task Config_save_bumps_version_and_detects_stale_writers()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            using var before = await client.GetAsync("/admin/v1/config");
            var beforeData = await ReadDataAsync(before);
            var version = long.Parse(beforeData.GetProperty("version").GetString()!);

            using var saved = await PutConfigAsync(client, version, ConfigPayload());
            var savedBody = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.StatusCode == HttpStatusCode.OK, savedBody);
            using var savedDocument = JsonDocument.Parse(savedBody);
            var savedData = savedDocument.RootElement.GetProperty("data").Clone();
            Assert.Equal(
                (version + 1).ToString(),
                savedData.GetProperty("version").GetString());
            Assert.True(savedData.GetProperty("requires_restart").GetBoolean());

            using var status = await client.GetAsync("/admin/v1/config/status");
            var statusData = await ReadDataAsync(status);
            Assert.Equal("requires_restart", statusData.GetProperty("state").GetString());
            Assert.Equal("0", statusData.GetProperty("applied_version").GetString());

            using var after = await client.GetAsync("/admin/v1/config");
            var afterData = await ReadDataAsync(after);
            Assert.Equal(
                7,
                afterData.GetProperty("values").GetProperty("runtime")
                    .GetProperty("sync_interval_seconds").GetInt32());

            using var stale = await PutConfigAsync(client, version, ConfigPayload());
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Config_secret_keep_replace_clear_never_returns_the_value()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            async Task<long> CurrentVersionAsync()
            {
                using var config = await client.GetAsync("/admin/v1/config");
                return long.Parse(
                    (await ReadDataAsync(config)).GetProperty("version").GetString()!);
            }

            async Task<JsonElement> CurrentConfigAsync()
            {
                using var config = await client.GetAsync("/admin/v1/config");
                return await ReadDataAsync(config);
            }

            var version = await CurrentVersionAsync();
            using (var cleared = await PutConfigAsync(
                       client, version, ConfigPayload(secretState: "clear")))
            {
                Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
                var data = await ReadDataAsync(cleared);
                Assert.False(data.GetProperty("secrets")
                    .GetProperty(AccessTokenPath).GetProperty("is_set").GetBoolean());
                Assert.DoesNotContain(AdapterFixture.ManagementSecret, data.GetRawText());
            }

            var replacement = "replacement-access-token-0123456789";
            version = await CurrentVersionAsync();
            using (var replaced = await PutConfigAsync(
                       client, version,
                       ConfigPayload(secretState: "replace", secretValue: replacement)))
            {
                Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
                var data = await ReadDataAsync(replaced);
                Assert.True(data.GetProperty("secrets")
                    .GetProperty(AccessTokenPath).GetProperty("is_set").GetBoolean());
                Assert.DoesNotContain(replacement, data.GetRawText());
            }

            var afterReplace = await CurrentConfigAsync();
            Assert.DoesNotContain(replacement, afterReplace.GetRawText());
            version = long.Parse(afterReplace.GetProperty("version").GetString()!);
            using (var clearedAgain = await PutConfigAsync(
                       client, version, ConfigPayload(secretState: "clear")))
            {
                Assert.Equal(HttpStatusCode.OK, clearedAgain.StatusCode);
                var data = await ReadDataAsync(clearedAgain);
                Assert.False(data.GetProperty("secrets")
                    .GetProperty(AccessTokenPath).GetProperty("is_set").GetBoolean());
            }
        }
    }

    [SkippableFact]
    public async Task Logs_and_metrics_expose_bounded_management_data()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            using var logs = await client.GetAsync("/admin/v1/logs?level=Trace&limit=5");
            Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
            var logData = await ReadDataAsync(logs);
            Assert.Equal(JsonValueKind.Array, logData.GetProperty("entries").ValueKind);
            var streamId = logData.GetProperty("stream_id").GetString();
            Assert.False(string.IsNullOrEmpty(streamId));
            Assert.False(string.IsNullOrEmpty(logData.GetProperty("stream_started_at").GetString()));
            foreach (var entry in logData.GetProperty("entries").EnumerateArray())
            {
                Assert.Equal(streamId, entry.GetProperty("stream_id").GetString());
                Assert.Equal(
                    logData.GetProperty("stream_started_at").GetString(),
                    entry.GetProperty("stream_started_at").GetString());
            }

            using var again = await client.GetAsync("/admin/v1/logs?level=Trace&limit=5");
            var againData = await ReadDataAsync(again);
            Assert.Equal(streamId, againData.GetProperty("stream_id").GetString());

            using var metrics = await client.GetAsync("/admin/v1/metrics");
            Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
            var metricData = await ReadDataAsync(metrics);
            Assert.Equal(
                "loopback",
                metricData.GetProperty("media").GetProperty("engine").GetString());
            Assert.Equal("0", metricData.GetProperty("config").GetProperty("applied_version").GetString());
        }
    }

    [SkippableFact]
    public async Task Cross_origin_write_is_rejected()
    {
        RequireStorage(_fixture);
        var (client, _) = await LoginAsync();
        using (client)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "/admin/v1/account/logout");
            request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
