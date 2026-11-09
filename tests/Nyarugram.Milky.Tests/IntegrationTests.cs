using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nyarugram.Milky.Core;
using Nyarugram.Milky.Host;
using Nyarugram.Milky.Storage;
using Npgsql;
using Xunit;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// End-to-end host test: a real HTTP Protobuf backend stand-in, a real PostgreSQL schema
/// and the real adapter endpoints. Requires a reachable PostgreSQL server; otherwise the
/// tests skip instead of passing vacuously.
/// </summary>
public sealed class AdapterFixture : IAsyncLifetime
{
    private const string DatabaseName = "nyarugram_milky_test";
    private string _adminConnection = "";
    public FakeBackend Backend { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string AccessToken { get; } = "test-access-token-0123456789";
    public const string ManagementSecret = "test-management-secret-0123456789";
    public Exception? StartupError { get; private set; }

    public static string AdminConnection =>
        Environment.GetEnvironmentVariable("NYARUGRAM_TEST_PG")
        ?? "Host=127.0.0.1;Port=56411;Database=postgres;Username=nyarugram";

    public async Task InitializeAsync()
    {
        try
        {
            _adminConnection = AdminConnection;
            await EnsureDatabaseAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            StartupError = ex;
            return;
        }

        var backendPort = FreePort();
        Backend = new FakeBackend(backendPort);
        await Backend.StartAsync();

        var listenPort = FreePort();
        var options = new MilkyOptions
        {
            BackendBaseAddress = new Uri(Backend.BaseAddress),
            ServerDomain = FakeBackend.ServerDomain,
            AllowPlaintextBackend = true,
            ConnectionString = Builder(DatabaseName),
            SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
            ListenUrl = $"http://127.0.0.1:{listenPort}",
            AccessToken = AccessToken,
            SyncInterval = TimeSpan.FromMinutes(5),
            MediaEngine = "loopback",
            BackgroundSync = false,
            BackgroundSendReconcile = false,
            SignalingMaxAttempts = 3,
            SignalingRetryBaseDelay = TimeSpan.FromMilliseconds(100),
            ManagementSecret = ManagementSecret,
            ManagementSecureCookie = false,
            AllowedOrigins = ["http://192.168.2.10:3456"],
        };

        App = Program.CreateApp(options, RepoRoot());
        var database = App.Services.GetRequiredService<MilkyDatabase>();
        await database.MigrateAsync(CancellationToken.None);
        await App.StartAsync();

        var sessions = App.Services.GetRequiredService<SessionManager>();
        var context = await sessions.LoginAsync("bot", "secret", null, CancellationToken.None);
        await App.Services.GetRequiredService<CallService>().ReconcileAsync(context, CancellationToken.None);
        Backend.Port.ToString();

        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listenPort}") };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
    }

    private async Task EnsureDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using (var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname=$1", connection))
        {
            check.Parameters.Add(new NpgsqlParameter { Value = DatabaseName });
            if (await check.ExecuteScalarAsync() is not null)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
            }
        }
        await using var create = new NpgsqlCommand($"CREATE DATABASE {DatabaseName}", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static string Builder(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnection) { Database = database };
        return builder.ConnectionString;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "locks.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
        if (Backend is not null) await Backend.DisposeAsync();
    }

    public async Task<JsonElement> CallAsync(string action, object? body = null)
    {
        var json = JsonSerializer.Serialize(body ?? new { });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync($"/api/{action}", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public MilkyRuntime Runtime => App.Services.GetRequiredService<MilkyRuntime>();

    public T Get<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public async Task SyncAsync() => await Get<SyncService>().RunOnceAsync(Runtime.Require(), CancellationToken.None);

    public async Task MaintainCallsAsync()
        => await Get<CallService>().MaintainAsync(Runtime.Require(), CancellationToken.None);

    /// <summary>
    /// Media readiness is completed off the sync loop, so a test that asserts the final
    /// media stage waits for it instead of assuming the sync tick carried it.
    /// </summary>
    public async Task<CallRecord> WaitForMediaStageAsync(Guid callId, string stage, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        CallRecord? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await Get<CallStore>().GetAsync(AccountId, callId, CancellationToken.None);
            if (last is not null && last.MediaStage == stage) return last;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException(
            $"call {callId} did not reach media stage {stage}; last={last?.MediaStage}/{last?.MediaState}");
    }

    public async Task<Guid?> CallIdForBackendAsync(long backendCallId)
    {
        var rows = await QueryAsync(
            "SELECT call_id FROM nyarugram_milky.call WHERE account_id=$1 AND backend_call_id=$2",
            AccountId, backendCallId);
        return rows.Count == 0 ? null : (Guid)rows[0]!;
    }

    public async Task<T?> ScalarAsync<T>(string sql, params object[] args)
    {
        var rows = await QueryAsync(sql, args);
        return rows.Count == 0 || rows[0] is null ? default : (T)rows[0]!;
    }

    public async Task<SessionContext> ReLoginAsync()
        => await Get<SessionManager>().LoginAsync("bot", "secret", null, CancellationToken.None);

    public Guid AccountId => Runtime.Require().Account.AccountId;

    /// <summary>Direct SQL for assertions the public API does not expose.</summary>
    public async Task ExecuteAsync(string sql, params object[] args)
    {
        await using var connection = new NpgsqlConnection(Builder(DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<object?>> QueryAsync(string sql, params object[] args)
    {
        await using var connection = new NpgsqlConnection(Builder(DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg });
        var rows = new List<object?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
        return rows;
    }

    public async Task<JsonElement> CallOkAsync(string action, object? body = null)
    {
        var envelope = await CallAsync(action, body);
        Assert.Equal("ok", envelope.GetProperty("status").GetString());
        Assert.Equal(0, envelope.GetProperty("retcode").GetInt32());
        return envelope.GetProperty("data");
    }
}

[CollectionDefinition("adapter")]
public sealed class AdapterCollection : ICollectionFixture<AdapterFixture>;

[Collection("adapter")]
public sealed class AdapterIntegrationTests
{
    private readonly AdapterFixture _fixture;

    public AdapterIntegrationTests(AdapterFixture fixture) => _fixture = fixture;

    private static void RequireStorage(AdapterFixture fixture)
    {
        if (fixture.StartupError is not null)
            throw new SkipException($"PostgreSQL is not reachable: {fixture.StartupError.Message}");
    }

    [SkippableFact]
    public async Task Login_info_reports_backend_uin_and_nickname()
    {
        RequireStorage(_fixture);
        var data = await _fixture.CallOkAsync("get_login_info");
        Assert.Equal(FakeBackend.AccountUin, data.GetProperty("uin").GetInt64());
        Assert.Equal(FakeBackend.AccountDisplayName, data.GetProperty("nickname").GetString());
    }

    [SkippableFact]
    public async Task Impl_info_declares_pinned_milky_version()
    {
        RequireStorage(_fixture);
        var data = await _fixture.CallOkAsync("get_impl_info");
        Assert.Equal("1.3", data.GetProperty("milky_version").GetString());
        Assert.Equal("Nyarugram.Milky", data.GetProperty("impl_name").GetString());
        Assert.NotEqual("", data.GetProperty("qq_protocol_version").GetString());
    }

    [SkippableFact]
    public async Task Capabilities_report_no_telephony_audio_for_loopback()
    {
        RequireStorage(_fixture);
        var data = await _fixture.CallOkAsync("nyarugram_get_capabilities");
        Assert.Equal(1, data.GetProperty("extension_version").GetInt32());
        Assert.False(data.GetProperty("calls").GetProperty("voice").GetBoolean());
        Assert.Equal("loopback", data.GetProperty("media").GetProperty("engine").GetString());
        Assert.Contains("message_receive", data.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
    }

    [SkippableFact]
    public async Task Friend_list_maps_contact_to_numeric_identity()
    {
        RequireStorage(_fixture);
        var data = await _fixture.CallOkAsync("get_friend_list");
        var friends = data.GetProperty("friends").EnumerateArray().ToList();
        Assert.Single(friends);
        Assert.Equal(FakeBackend.FriendUin, friends[0].GetProperty("user_id").GetInt64());
        Assert.Equal(FakeBackend.FriendDisplayName, friends[0].GetProperty("nickname").GetString());
        Assert.Equal("unknown", friends[0].GetProperty("sex").GetString());
    }

    [SkippableFact]
    public async Task Group_list_and_members_come_from_dialogs_and_full_group()
    {
        RequireStorage(_fixture);
        var groups = await _fixture.CallOkAsync("get_group_list");
        var group = groups.GetProperty("groups").EnumerateArray().Single();
        Assert.Equal(FakeBackend.GroupUin, group.GetProperty("group_id").GetInt64());
        Assert.Equal("Test Group", group.GetProperty("group_name").GetString());

        var members = await _fixture.CallOkAsync("get_group_member_list", new { group_id = FakeBackend.GroupUin });
        var list = members.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, m => m.GetProperty("user_id").GetInt64() == FakeBackend.FriendUin);
    }

    [SkippableFact]
    public async Task Send_private_message_returns_backend_message_sequence()
    {
        RequireStorage(_fixture);
        var data = await _fixture.CallOkAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "text", data = new { text = "hello" } } },
        });
        Assert.True(data.GetProperty("message_seq").GetInt64() >= 1000);
        Assert.True(data.GetProperty("time").GetInt64() > 0);
    }

    [SkippableFact]
    public async Task Unsupported_segment_is_rejected_with_unsupported_retcode()
    {
        RequireStorage(_fixture);
        var envelope = await _fixture.CallAsync("send_private_message", new
        {
            user_id = FakeBackend.FriendUin,
            message = new object[] { new { type = "image", data = new { file_id = "x" } } },
        });
        Assert.Equal("failed", envelope.GetProperty("status").GetString());
        Assert.Equal(-501, envelope.GetProperty("retcode").GetInt32());
    }

    [SkippableFact]
    public async Task Unknown_action_returns_404_and_missing_token_returns_401()
    {
        RequireStorage(_fixture);
        var response = await _fixture.Client.PostAsync("/api/not_an_action",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var anonymous = new HttpClient { BaseAddress = _fixture.Client.BaseAddress };
        var unauthorized = await anonymous.PostAsync("/api/get_login_info",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [SkippableFact]
    public async Task Incoming_message_becomes_message_receive_event_over_sse()
    {
        RequireStorage(_fixture);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/event");
        using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        _fixture.Backend.InjectFriendMessage("ping from alice");
        var sync = _fixture.App.Services.GetRequiredService<SyncService>();
        var runtime = _fixture.App.Services.GetRequiredService<MilkyRuntime>();
        await sync.RunOnceAsync(runtime.Require(), CancellationToken.None);

        var payload = await ReadEventAsync(reader, TimeSpan.FromSeconds(15));
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("message_receive", root.GetProperty("event_type").GetString());
        Assert.Equal(FakeBackend.AccountUin, root.GetProperty("self_id").GetInt64());
        var data = root.GetProperty("data");
        Assert.Equal("friend", data.GetProperty("message_scene").GetString());
        Assert.Equal(FakeBackend.FriendUin, data.GetProperty("peer_id").GetInt64());
        Assert.Equal(FakeBackend.FriendUin, data.GetProperty("sender_id").GetInt64());
        var segment = data.GetProperty("segments").EnumerateArray().First();
        Assert.Equal("text", segment.GetProperty("type").GetString());
        Assert.Equal("ping from alice", segment.GetProperty("data").GetProperty("text").GetString());
    }

    [SkippableFact]
    public async Task Cancelling_the_caller_clears_busy_state_and_is_idempotent()
    {
        RequireStorage(_fixture);

        var started = await _fixture.CallOkAsync("nyarugram_start_call", new
        {
            user_id = FakeBackend.FriendUin,
            request_id = "call-req-1",
            video = false,
        });
        var call = started.GetProperty("call");
        var callId = call.GetProperty("call_id").GetString();
        Assert.Equal("outgoing", call.GetProperty("direction").GetString());

        var ended = await _fixture.CallOkAsync("nyarugram_end_call", new
        {
            call_id = callId,
            reason = "hangup",
            request_id = "end-1",
        });
        Assert.Equal("ended", ended.GetProperty("call").GetProperty("state").GetString());

        var again = await _fixture.CallOkAsync("nyarugram_end_call", new
        {
            call_id = callId,
            reason = "hangup",
        });
        Assert.Equal("ended", again.GetProperty("call").GetProperty("state").GetString());
    }

    private static async Task<string> ReadEventAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        string? data = null;
        while (!cancellation.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null) throw new InvalidOperationException("event stream closed");
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line[6..];
            else if (line.Length == 0 && data is not null)
                return data;
        }
        throw new TimeoutException("no Milky event received");
    }
}
