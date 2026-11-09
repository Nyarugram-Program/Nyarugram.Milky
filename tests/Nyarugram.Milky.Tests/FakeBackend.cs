using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Backend;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Tests;

/// <summary>
/// In-process stand-in for a Nyarugram backend. It mirrors the real endpoint split:
/// account operations are only accepted on /api/v1/account, business operations only on
/// /api/v1/rpc. It also implements the phone.* state machine and the random-id dedupe the
/// real backend performs, so adapter idempotency is exercised against real semantics.
/// </summary>
public sealed class FakeBackend : IAsyncDisposable
{
    public const string Token = "fake-bearer-token";
    public const string ServerDomain = "test.local";
    public const string AccountUuid = "account-uuid";
    public const string AccountUsername = "bot";
    public const string AccountDisplayName = "Bot";
    public const long AccountUin = 100_001;

    public const string SecondAccountUuid = "account-uuid-2";
    public const string SecondAccountUsername = "bot2";
    public const string SecondAccountDisplayName = "Bot Two";
    public const long SecondAccountUin = 100_005;

    public const string FriendUuid = "friend-uuid";
    public const string FriendUsername = "alice";
    public const string FriendDisplayName = "Alice";
    public const long FriendUin = 100_002;

    public const string GroupUuid = "group-uuid";
    public const long GroupUin = 100_003;

    public const string BobUuid = "bob-uuid";
    public const string BobUsername = "bob";
    public const long BobUin = 100_004;

    // Telegram's 2048-bit DH prime: 256 bytes, matching the backend contract.
    public static readonly byte[] DhPrime = Convert.FromHexString(
        "c71caeb9c6b1c9048e6c522f70f13f73980d40238e3e21c14934d037563d930f" +
        "48198a0aa7c14058229493d22530f4dbfa336f6e0ac925139543aed44cce7c3720" +
        "fd51f69458705ac68cd4fe6b6b13abdc9746512969328454f18faf8c595f64247" +
        "7fe96bb2a941d5bcd1d4ac8cc49880708fa9b378e3c4f3a9060bee67cf9a4a4a" +
        "695811051907e162753b56b0f6b410dba74d8a84b2a14b3144e0ef1284754fd17" +
        "ed950d5965b4b9dd46582db1178d169c6bc465b0d6ff9ca3928fef5b9ae4e418" +
        "fc15e83ebea0f87fa9ff5eed70050ded2849f47bf959d956850ce929851f0d8115" +
        "f635b105ee2e4e15d04b2454bf6f4fadf034b10403119cd8e3b92fcc5b");

    private sealed record FakeEvent(int Pts, P.Message? Message, P.PhoneCall? Call);

    private sealed class FakeCall
    {
        public long Id;
        public long AccessHash;
        public long CallerTl;
        public long CalleeTl;
        public string State = "ringing";
        public long RandomId;
        public bool Video;
        public byte[] GaHash = [];
        public byte[] Gb = [];
        public byte[] Ga = [];
        public long Fingerprint;
        public int ReceiveDate;
        public bool P2p;
        public byte[] CallerSecret = [];
        public byte[] PublishedGa = [];
    }

    private readonly WebApplication _app;
    private readonly List<FakeEvent> _events = [];
    private readonly Dictionary<long, FakeCall> _calls = new();
    private readonly object _gate = new();
    private int _nextMessageId = 1000;
    private long _nextCallId = 5000;
    private int _pts;

    /// <summary>Injected fault for the next business RPC, used to model dropped responses.</summary>
    private (P.RpcRequest.OperationOneofCase? Operation, int Status, string Code, bool AfterCommit, bool Abort)? _nextFault;

    /// <summary>A fault that persists for a bounded number of matching operations.</summary>
    private (P.RpcRequest.OperationOneofCase? Operation, int Status, string Code, int Remaining)? _repeatedFault;
    private readonly object _faultGate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _signalingPayloads = new();

    /// <summary>Engine signals the backend received, in delivery order.</summary>
    public string[] SignalingPayloads => _signalingPayloads.ToArray();

    public FakeBackend(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapPost("/api/v1/auth", HandleAuth);
        _app.MapPost("/api/v1/account", HandleAccount);
        _app.MapPost("/api/v1/rpc", HandleRpc);
        _app.MapMethods("/api/ws", ["GET"], HandleWs);
        Port = port;
    }

    public int Port { get; }
    public string BaseAddress => $"http://127.0.0.1:{Port}";

    /// <summary>Delays /api/v1/account responses so a stale-generation test can interleave.</summary>
    public TimeSpan AccountDelay { get; set; } = TimeSpan.Zero;

    public Task StartAsync() => _app.StartAsync();

    public void FailNextRpc(int status, string code, bool afterCommit = false)
        => _nextFault = (null, status, code, afterCommit, false);

    /// <summary>Fails only the named operation, so unrelated lookups are unaffected.</summary>
    public void FailOperation(P.RpcRequest.OperationOneofCase operation, int status, string code)
        => _nextFault = (operation, status, code, false, false);

    public void DropOperationResponse(P.RpcRequest.OperationOneofCase operation)
        => _nextFault = (operation, 0, "transport_dropped", true, true);

    /// <summary>Fails the named operation for a bounded number of attempts.</summary>
    public void FailOperationTimes(
        P.RpcRequest.OperationOneofCase operation, int status, string code, int times)
    {
        lock (_faultGate) _repeatedFault = (operation, status, code, times);
    }

    public void ClearRepeatedFault()
    {
        lock (_faultGate) _repeatedFault = null;
    }

    /// <summary>
    /// Commits the operation and then drops the connection, which is what a lost response
    /// looks like to the client (no status, no body).
    /// </summary>
    public void DropNextResponseAfterCommit() => _nextFault = (null, 0, "transport_dropped", true, true);

    public int CallCount
    {
        get { lock (_gate) return _calls.Count; }
    }

    public long LatestCallId
    {
        get { lock (_gate) return _calls.Count == 0 ? 0 : _calls.Keys.Max(); }
    }

    public int CurrentPts
    {
        get { lock (_gate) return _pts; }
    }

    /// <summary>Operation and explicit foreground flag of the most recent business RPC.</summary>
    public (P.RpcRequest.OperationOneofCase Operation, bool? Foreground)? LastBusinessRequest { get; private set; }

    public P.Message InjectFriendMessage(string text)
    {
        lock (_gate)
        {
            var message = new P.Message
            {
                MessageId = _nextMessageId++,
                Peer = new P.PeerRef { User = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid } },
                Sender = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Outgoing = false,
                Unread = true,
                Text = new P.TextMessage { Text = text },
            };
            _events.Add(new FakeEvent(++_pts + 1, message, null));
            return message;
        }
    }

    /// <summary>Injects a message the adapter cannot represent (encrypted body).</summary>
    public P.Message InjectEncryptedMessage()
    {
        lock (_gate)
        {
            var message = new P.Message
            {
                MessageId = _nextMessageId++,
                Peer = new P.PeerRef { User = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid } },
                Sender = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Outgoing = false,
                E2Ee = new P.E2eeEnvelope { ProtocolVersion = 1, Ciphertext = ByteString.CopyFrom([1, 2, 3]) },
            };
            _events.Add(new FakeEvent(++_pts + 1, message, null));
            return message;
        }
    }

    /// <summary>Valid DH public value derived from the pinned prime and generator.</summary>
    public static byte[] DerivePublic(byte[] secret)
    {
        var p = new BigInteger(DhPrime, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
        var value = BigInteger.ModPow(3, exponent, p);
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[256];
        bytes.CopyTo(padded, 256 - bytes.Length);
        return padded;
    }

    /// <summary>Replaces the callee public value with an arbitrary (possibly hostile) one.</summary>
    public void SetCallPublicValue(long callId, byte[] gb)
    {
        lock (_gate) _calls[callId].Gb = gb;
    }

    /// <summary>
    /// Creates an inbound call for this account with the caller's g_a commitment, then emits
    /// the ringing update so the adapter can observe it through the normal difference path.
    /// </summary>
    public long InjectIncomingCall(byte[]? gaHash = null, long callerUin = BobUin, bool video = false)
    {
        lock (_gate)
        {
            var secret = RandomNumberGenerator.GetBytes(256);
            var ga = DerivePublic(secret);
            var call = new FakeCall
            {
                Id = _nextCallId++,
                AccessHash = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                CallerTl = callerUin,
                CalleeTl = AccountUin,
                RandomId = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                Video = video,
                State = "ringing",
                GaHash = gaHash ?? SHA256.HashData(ga),
                Ga = ga,
                CallerSecret = secret,
            };
            _calls[call.Id] = call;
            _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call)));
            return call.Id;
        }
    }

    /// <summary>
    /// Completes an inbound call the way a caller's confirm_call would: publishes g_a and a
    /// fingerprint computed from the real shared secret (g_b^a), then moves to active.
    /// </summary>
    public void CompleteIncomingCall(
        long callId, byte[]? gaOverride = null, long? fingerprintOverride = null, bool publishGa = true)
    {
        lock (_gate)
        {
            var call = _calls[callId];
            if (gaOverride is not null) call.Ga = gaOverride;
            if (publishGa) call.PublishedGa = call.Ga;

            long fingerprint = 0;
            if (call.Gb.Length == 256 && call.CallerSecret.Length == 256)
            {
                var p = new BigInteger(DhPrime, isUnsigned: true, isBigEndian: true);
                var gb = new BigInteger(call.Gb, isUnsigned: true, isBigEndian: true);
                var exponent = new BigInteger(call.CallerSecret, isUnsigned: true, isBigEndian: true);
                var key = BigInteger.ModPow(gb, exponent, p);
                var bytes = key.ToByteArray(isUnsigned: true, isBigEndian: true);
                var padded = new byte[256];
                bytes.CopyTo(padded, 256 - bytes.Length);
                var hash = SHA1.HashData(padded);
                fingerprint = BitConverter.ToInt64(hash, hash.Length - 8);
            }
            call.Fingerprint = fingerprintOverride ?? fingerprint;
            call.State = "active";
            _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call)));
        }
    }

    /// <summary>Overrides the g_a published to the callee without touching the commitment.</summary>
    public void SetCallPeerGa(long callId, byte[] ga)
    {
        lock (_gate) _calls[callId].Ga = ga;
    }

    /// <summary>Advances a call to a new state and emits the corresponding update.</summary>
    public void AdvanceCall(long callId, string state, P.PhoneCallDiscardReason reason = P.PhoneCallDiscardReason.Unspecified)
    {
        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out var call)) return;
            call.State = state;
            // Simulate the remote client's accept with a real DH public value: g^b mod p.
            if (state == "accepted" && call.Gb.Length == 0)
                call.Gb = DerivePublic(RandomNumberGenerator.GetBytes(256));
            _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call, reason)));
        }
    }

    public FakeCallSnapshot GetCall(long callId)
    {
        lock (_gate)
        {
            var call = _calls[callId];
            return new FakeCallSnapshot(call.Id, call.State, call.RandomId, call.GaHash, call.Gb, call.Ga, call.Fingerprint);
        }
    }

    public sealed record FakeCallSnapshot(
        long Id, string State, long RandomId, byte[] GaHash, byte[] Gb, byte[] Ga, long Fingerprint);

    private static P.UserSummary FriendSummary() => new()
    {
        Identity = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
        TlUserId = FriendUin,
        Username = FriendUsername,
        DisplayName = FriendDisplayName,
        FirstName = "Alice",
        Gid = "+8613800000001",
    };

    private static P.UserSummary BobSummary() => new()
    {
        Identity = new P.UserRef { ServerDomain = ServerDomain, UserId = BobUuid },
        TlUserId = BobUin,
        Username = BobUsername,
        DisplayName = "Bob",
        FirstName = "Bob",
    };

    private static P.UserSummary SelfSummary() => new()
    {
        Identity = new P.UserRef { ServerDomain = ServerDomain, UserId = AccountUuid },
        TlUserId = AccountUin,
        Username = AccountUsername,
        DisplayName = AccountDisplayName,
        IsSelf = true,
    };

    private static P.GroupSummary GroupSummary() => new()
    {
        GroupId = GroupUuid,
        TlChatId = GroupUin,
        Title = "Test Group",
        ParticipantCount = 2,
        CreatedAtMs = 1_700_000_000_000,
    };

    private static P.AccountProfile SelfProfile() => new()
    {
        UserId = AccountUuid,
        Username = AccountUsername,
        DisplayName = AccountDisplayName,
        TlUserId = AccountUin,
        IsSelf = true,
        Online = true,
    };

    private static P.AccountProfile SecondSelfProfile() => new()
    {
        UserId = SecondAccountUuid,
        Username = SecondAccountUsername,
        DisplayName = SecondAccountDisplayName,
        TlUserId = SecondAccountUin,
        IsSelf = true,
        Online = true,
    };

    private static P.PhoneCall Snapshot(FakeCall call, P.PhoneCallDiscardReason reason = P.PhoneCallDiscardReason.Unspecified)
    {
        var snapshot = new P.PhoneCall
        {
            CallId = call.Id,
            AccessHash = call.AccessHash,
            State = call.State switch
            {
                "ringing" => P.PhoneCallState.Requested,
                "accepted" => P.PhoneCallState.Accepted,
                "active" => P.PhoneCallState.Active,
                "ended" => P.PhoneCallState.Discarded,
                _ => P.PhoneCallState.Requested,
            },
            AdminTlUserId = call.CallerTl,
            ParticipantTlUserId = call.CalleeTl,
            DateMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Video = call.Video,
            P2PAllowed = call.P2p,
            GAHash = ByteString.CopyFrom(call.GaHash),
            GB = ByteString.CopyFrom(call.Gb),
            GAOrB = ByteString.CopyFrom(call.PublishedGa.Length > 0 ? call.PublishedGa : call.Ga),
            KeyFingerprint = call.Fingerprint,
            DiscardReason = reason,
            ReceiveDateMs = call.ReceiveDate,
        };
        if (call.State == "active")
        {
            snapshot.Connections.Add(new P.PhoneConnection
            {
                Id = 1, Ip = "127.0.0.1", Port = 4000, Turn = false, Stun = true,
            });
        }
        return snapshot;
    }

    // ------------------------------------------------------------------ auth

    private async Task HandleAuth(HttpContext context)
    {
        var request = await ReadAsync(context);
        var response = new P.RpcResponse { ProtocolMajor = 1, RequestId = request.RequestId };
        switch (request.OperationCase)
        {
            case P.RpcRequest.OperationOneofCase.AuthCapabilities:
                response.AuthCapabilities = new P.AuthCapabilities
                {
                    NegotiationVersion = 1,
                    Protocols = { "protobuf-v1" },
                    SelectedProtocol = "protobuf-v1",
                    EventSubprotocols = { BackendClient.EventSubProtocol },
                    JsonApiStatus = "BOTGRAM_JSON_API_RETIRED",
                };
                break;
            case P.RpcRequest.OperationOneofCase.AuthLogin:
                if (request.AuthLogin.Username == "locked")
                {
                    await WriteError(context, 403, "account_locked", request.RequestId);
                    return;
                }
                var second = request.AuthLogin.Username == SecondAccountUsername
                    && request.AuthLogin.Password == "secret";
                if (!second && (request.AuthLogin.Username != AccountUsername || request.AuthLogin.Password != "secret"))
                {
                    await WriteError(context, 401, "unauthorized", request.RequestId);
                    return;
                }
                response.AuthLogin = new P.AuthSession
                {
                    Token = second ? Token + ":" + SecondAccountUsername : Token,
                    ExpiresAtMs = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                    User = second ? SecondSelfProfile() : SelfProfile(),
                };
                break;
            default:
                // The real auth plane rejects business/account operations outright.
                await WriteError(context, 400, "BOTGRAM_AUTH_PLANE_OPERATION_INVALID", request.RequestId);
                return;
        }
        await WriteAsync(context, response);
    }

    // --------------------------------------------------------------- account

    private async Task HandleAccount(HttpContext context)
    {
        if (!Authorized(context))
        {
            context.Response.StatusCode = 401;
            return;
        }
        if (AccountDelay > TimeSpan.Zero) await Task.Delay(AccountDelay, context.RequestAborted);

        var request = await ReadAsync(context);
        var response = new P.RpcResponse { ProtocolMajor = 1, RequestId = request.RequestId };
        switch (request.OperationCase)
        {
            case P.RpcRequest.OperationOneofCase.AccountMe:
                response.AccountMe = IsSecondAccount(context) ? SecondSelfProfile() : SelfProfile();
                break;
            case P.RpcRequest.OperationOneofCase.AuthLogout:
                response.AuthLogout = new P.BoolValue { Value = true };
                break;
            default:
                await WriteError(context, 400, "BOTGRAM_AUTH_PLANE_OPERATION_INVALID", request.RequestId);
                return;
        }
        await WriteAsync(context, response);
    }

    // ------------------------------------------------------------------ rpc

    private async Task HandleRpc(HttpContext context)
    {
        if (!Authorized(context))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var request = await ReadAsync(context);
        var response = new P.RpcResponse { ProtocolMajor = 1, RequestId = request.RequestId };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        LastBusinessRequest = (request.OperationCase, request.HasForeground ? request.Foreground : null);

        var fault = _nextFault;
        if (fault is { } candidate
            && (candidate.Operation is null || candidate.Operation == request.OperationCase))
            _nextFault = null;
        else
            fault = null;
        if (fault is { Abort: false, AfterCommit: false } injected)
        {
            await WriteError(context, injected.Status, injected.Code, request.RequestId);
            return;
        }

        (P.RpcRequest.OperationOneofCase? Operation, int Status, string Code)? repeated = null;
        lock (_faultGate)
        {
            if (_repeatedFault is { } repeatedFault
                && (repeatedFault.Operation is null || repeatedFault.Operation == request.OperationCase))
            {
                repeated = (repeatedFault.Operation, repeatedFault.Status, repeatedFault.Code);
                _repeatedFault = repeatedFault.Remaining > 1
                    ? (repeatedFault.Operation, repeatedFault.Status, repeatedFault.Code, repeatedFault.Remaining - 1)
                    : null;
            }
        }
        if (repeated is { } failure)
        {
            await WriteError(context, failure.Status, failure.Code, request.RequestId);
            return;
        }

        switch (request.OperationCase)
        {
            case P.RpcRequest.OperationOneofCase.AccountMe:
                // Account operations are not served on the business endpoint.
                await WriteError(context, 501, "BOTGRAM_RPC_UNSUPPORTED", request.RequestId);
                return;

            case P.RpcRequest.OperationOneofCase.GetDifference:
            {
                lock (_gate)
                {
                    var upper = _pts == 0 ? 1 : _pts + 1;
                    if (request.GetDifference.Pts > upper)
                    {
                        response.GetDifference = new P.Difference { TooLong = new P.DifferenceTooLong { Pts = upper } };
                        break;
                    }
                    var page = _events.Where(e => e.Pts > Math.Max(1, request.GetDifference.Pts)).Take(100).ToList();
                    if (page.Count == 0)
                    {
                        response.GetDifference = new P.Difference { Empty = new P.DifferenceEmpty { DateMs = now } };
                        break;
                    }
                    var difference = new P.DifferencePage { State = new P.SyncState { Pts = page[^1].Pts, DateMs = now } };
                    difference.Messages.AddRange(page.Where(e => e.Message is not null).Select(e => e.Message!));
                    difference.Updates.AddRange(page.Where(e => e.Call is not null)
                        .Select(e => new P.Update { PhoneCall = new P.PhoneCallChanged { PhoneCall = e.Call } }));
                    difference.Groups.Add(GroupSummary());
                    difference.Users.Add(FriendSummary());
                    // A full page means more events may remain: report a slice so the adapter
                    // keeps polling until the whole backlog is drained instead of stalling on a
                    // single 100-entry page.
                    if (page.Count >= 100)
                        response.GetDifference = new P.Difference { Slice = difference };
                    else
                        response.GetDifference = new P.Difference { Complete = difference };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.GetState:
                response.GetState = new P.SyncState { Pts = _pts, DateMs = now };
                break;
            case P.RpcRequest.OperationOneofCase.GetContacts:
                response.GetContacts = new P.ContactList { SavedCount = 1 };
                response.GetContacts.Contacts.Add(new P.Contact
                {
                    User = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
                    Mutual = true,
                });
                response.GetContacts.Users.Add(FriendSummary());
                break;
            case P.RpcRequest.OperationOneofCase.GetDialogs:
                response.GetDialogs = new P.DialogPage { TotalCount = 1 };
                response.GetDialogs.Dialogs.Add(new P.Dialog { Peer = new P.PeerRef { GroupId = GroupUuid } });
                response.GetDialogs.Groups.Add(GroupSummary());
                response.GetDialogs.Users.Add(FriendSummary());
                break;
            case P.RpcRequest.OperationOneofCase.GetUsers:
                response.GetUsers = new P.UserList();
                if (IsSelf(request.GetUsers.Users)) response.GetUsers.Users.Add(SelfSummary());
                else if (IsBob(request.GetUsers.Users)) response.GetUsers.Users.Add(BobSummary());
                else response.GetUsers.Users.Add(FriendSummary());
                break;
            case P.RpcRequest.OperationOneofCase.GetGroups:
                response.GetGroups = new P.GroupList();
                response.GetGroups.Groups.Add(GroupSummary());
                break;
            case P.RpcRequest.OperationOneofCase.ResolveIdentities:
                response.ResolveIdentities = new P.ResolvedIdentities { ServerDomain = ServerDomain };
                if (request.ResolveIdentities.TlUserIds.Contains(AccountUin))
                    response.ResolveIdentities.Users.Add(SelfSummary());
                if (request.ResolveIdentities.TlUserIds.Contains(FriendUin))
                    response.ResolveIdentities.Users.Add(FriendSummary());
                if (request.ResolveIdentities.TlUserIds.Contains(BobUin))
                    response.ResolveIdentities.Users.Add(BobSummary());
                if (request.ResolveIdentities.TlChatIds.Contains(GroupUin))
                    response.ResolveIdentities.Groups.Add(GroupSummary());
                break;
            case P.RpcRequest.OperationOneofCase.GetFullGroup:
                response.GetFullGroup = new P.GroupFullResult { Group = GroupSummary(), ParticipantsVersion = 1, About = "group about" };
                response.GetFullGroup.Participants.Add(new P.Participant
                {
                    User = new P.UserRef { ServerDomain = ServerDomain, UserId = AccountUuid },
                    JoinedAtMs = 1_700_000_000_000,
                });
                response.GetFullGroup.Participants.Add(new P.Participant
                {
                    User = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
                    JoinedAtMs = 1_700_000_100_000,
                });
                response.GetFullGroup.Users.Add(SelfSummary());
                response.GetFullGroup.Users.Add(FriendSummary());
                break;
            case P.RpcRequest.OperationOneofCase.GetFullUser:
                response.GetFullUser = new P.UserFullResult
                {
                    User = new P.UserRef { ServerDomain = ServerDomain, UserId = FriendUuid },
                    About = "friend bio",
                };
                response.GetFullUser.Users.Add(FriendSummary());
                break;
            case P.RpcRequest.OperationOneofCase.SendMessage:
            {
                var sent = new P.Message
                {
                    MessageId = _nextMessageId++,
                    Peer = request.SendMessage.Peer,
                    Sender = new P.UserRef { ServerDomain = ServerDomain, UserId = AccountUuid },
                    CreatedAtMs = now,
                    Outgoing = true,
                    Text = new P.TextMessage
                    {
                        Text = request.SendMessage.Text,
                        Reply = request.SendMessage.HasReplyToMessageId
                            ? new P.ReplyRef { MessageId = request.SendMessage.ReplyToMessageId }
                            : null,
                        Entities = { request.SendMessage.Entities },
                    },
                };
                response.SendMessage = new P.MessageSent
                {
                    Message = sent,
                    Mapping = new P.MessageIdMapping
                    {
                        RandomId = request.SendMessage.RandomId,
                        MessageId = sent.MessageId,
                    },
                };
                // The response carries an Updates block whose pts is the send event's own
                // pts; a correct adapter must not feed it into the sync cursor.
                response.SendMessage.Updates = new P.Updates { DateMs = now, Seq = 99 };
                break;
            }
            case P.RpcRequest.OperationOneofCase.GetMessages:
                response.GetMessages = new P.MessagePage();
                lock (_gate)
                {
                    foreach (var id in request.GetMessages.MessageIds)
                    {
                        var match = _events.FirstOrDefault(e => e.Message is not null && e.Message.MessageId == id);
                        if (match?.Message is not null) response.GetMessages.Messages.Add(match.Message);
                    }
                }
                break;
            case P.RpcRequest.OperationOneofCase.GetHistory:
                response.GetHistory = new P.MessagePage();
                lock (_gate) response.GetHistory.Messages.AddRange(_events.Where(e => e.Message is not null).Select(e => e.Message!));
                break;
            case P.RpcRequest.OperationOneofCase.ReadHistory:
                response.ReadHistory = new P.AffectedMessages { Pts = 1, PtsCount = 1 };
                break;

            // ---------------------------------------------------------- calls

            case P.RpcRequest.OperationOneofCase.RequestCall:
            {
                lock (_gate)
                {
                    var existing = _calls.Values.FirstOrDefault(c => c.CallerTl == AccountUin && c.RandomId == request.RequestCall.RandomId);
                    if (existing is not null)
                    {
                        if (existing.CalleeTl != FriendUin || existing.Video != request.RequestCall.Video
                            || !existing.GaHash.AsSpan().SequenceEqual(request.RequestCall.GAHash.Span))
                        {
                            response.Error = new P.RpcError
                            {
                                Code = "CALL_IDEMPOTENCY_CONFLICT", Message = "CALL_IDEMPOTENCY_CONFLICT",
                                HttpStatus = 409,
                            };
                            break;
                        }
                        response.RequestCall = new P.PhoneCallResult { PhoneCall = Snapshot(existing) };
                        break;
                    }
                    var hash = request.RequestCall.GAHash.ToByteArray();
                    if (hash.Length != 32)
                    {
                        response.Error = new P.RpcError { Code = "CALL_HASH_MISMATCH", Message = "CALL_HASH_MISMATCH", HttpStatus = 400 };
                        break;
                    }
                    if (request.RequestCall.RandomId == 0)
                    {
                        response.Error = new P.RpcError { Code = "CALL_RANDOM_ID_INVALID", Message = "CALL_RANDOM_ID_INVALID", HttpStatus = 400 };
                        break;
                    }
                    var call = new FakeCall
                    {
                        Id = _nextCallId++,
                        AccessHash = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                        CallerTl = AccountUin,
                        CalleeTl = FriendUin,
                        RandomId = request.RequestCall.RandomId,
                        Video = request.RequestCall.Video,
                        GaHash = hash,
                        P2p = false,
                        State = "ringing",
                    };
                    _calls[call.Id] = call;
                    _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call)));
                    response.RequestCall = new P.PhoneCallResult { PhoneCall = Snapshot(call) };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.ReceivedCall:
            {
                lock (_gate)
                {
                    if (!_calls.TryGetValue(request.ReceivedCall.Peer.CallId, out var call) || call.CalleeTl != AccountUin)
                    {
                        response.Error = new P.RpcError { Code = "CALL_NOT_FOUND", Message = "CALL_NOT_FOUND", HttpStatus = 400 };
                        break;
                    }
                    if (call.ReceiveDate == 0) call.ReceiveDate = (int)(now / 1000);
                    response.ReceivedCall = new P.BoolValue { Value = true };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.AcceptCall:
            {
                lock (_gate)
                {
                    if (!_calls.TryGetValue(request.AcceptCall.Peer.CallId, out var call))
                    {
                        response.Error = new P.RpcError { Code = "CALL_NOT_FOUND", Message = "CALL_NOT_FOUND", HttpStatus = 400 };
                        break;
                    }
                    if (request.AcceptCall.GB.Length != 256)
                    {
                        response.Error = new P.RpcError { Code = "CALL_INVALID_GB", Message = "CALL_INVALID_GB", HttpStatus = 400 };
                        break;
                    }
                    call.Gb = request.AcceptCall.GB.ToByteArray();
                    call.State = "accepted";
                    _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call)));
                    response.AcceptCall = new P.PhoneCallResult { PhoneCall = Snapshot(call) };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.ConfirmCall:
            {
                lock (_gate)
                {
                    if (!_calls.TryGetValue(request.ConfirmCall.Peer.CallId, out var call))
                    {
                        response.Error = new P.RpcError { Code = "CALL_NOT_FOUND", Message = "CALL_NOT_FOUND", HttpStatus = 400 };
                        break;
                    }
                    var ga = request.ConfirmCall.GA.ToByteArray();
                    if (ga.Length != 256 || !SHA256.HashData(ga).AsSpan().SequenceEqual(call.GaHash))
                    {
                        response.Error = new P.RpcError { Code = "CALL_HASH_MISMATCH", Message = "CALL_HASH_MISMATCH", HttpStatus = 400 };
                        break;
                    }
                    if (request.ConfirmCall.KeyFingerprint == 0)
                    {
                        response.Error = new P.RpcError { Code = "CALL_INVALID_KEY_FINGERPRINT", Message = "CALL_INVALID_KEY_FINGERPRINT", HttpStatus = 400 };
                        break;
                    }
                    call.Ga = ga;
                    call.Fingerprint = request.ConfirmCall.KeyFingerprint;
                    call.State = "active";
                    _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call)));
                    response.ConfirmCall = new P.PhoneCallResult { PhoneCall = Snapshot(call) };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.DiscardCall:
            {
                lock (_gate)
                {
                    if (!_calls.TryGetValue(request.DiscardCall.Peer.CallId, out var call))
                    {
                        response.Error = new P.RpcError { Code = "CALL_NOT_FOUND", Message = "CALL_NOT_FOUND", HttpStatus = 400 };
                        break;
                    }
                    if (call.State != "ended")
                    {
                        call.State = "ended";
                        _events.Add(new FakeEvent(++_pts + 1, null, Snapshot(call, request.DiscardCall.Reason)));
                    }
                    response.DiscardCall = new P.Updates { DateMs = now };
                }
                break;
            }
            case P.RpcRequest.OperationOneofCase.SendSignalingData:
                _signalingPayloads.Enqueue(request.SendSignalingData.Data.ToStringUtf8());
                response.SendSignalingData = new P.BoolValue { Value = true };
                break;
            case P.RpcRequest.OperationOneofCase.GetCallConfig:
                response.GetCallConfig = new P.DataJson { Data = "{\"engine\":\"fake\"}" };
                break;
            case P.RpcRequest.OperationOneofCase.GetDhConfig:
                response.GetDhConfig = new P.DhConfig
                {
                    Version = 1,
                    G = 3,
                    P = ByteString.CopyFrom(DhPrime),
                    Random = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(256)),
                };
                break;
            default:
                await WriteError(context, 501, "BOTGRAM_RPC_UNSUPPORTED", request.RequestId);
                return;
        }

        // After-commit faults model a lost response for an operation the backend already
        // applied. They apply to every mutating operation, not just sends.
        if (fault is { AfterCommit: true } committed)
        {
            if (committed.Abort) context.Abort();
            else await WriteError(context, committed.Status, committed.Code, request.RequestId);
            return;
        }

        await WriteAsync(context, response);
    }

    private async Task HandleWs(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync(BackendClient.EventSubProtocol);
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                long cursor;
                lock (_gate) cursor = _pts == 0 ? 1 : _pts + 1;
                var hint = new P.EventHint { ProtocolMajor = 1, Cursor = cursor };
                await socket.SendAsync(hint.ToByteArray(), WebSocketMessageType.Binary, true, context.RequestAborted);
                await Task.Delay(200, context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static bool IsSelf(IEnumerable<P.UserSelector> selectors)
        => selectors.Any(s => s.TargetCase == P.UserSelector.TargetOneofCase.Self || s.User?.UserId == AccountUuid);

    private static bool IsBob(IEnumerable<P.UserSelector> selectors)
        => selectors.Any(s => s.User?.UserId == BobUuid);

    private static bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return string.Equals(header, "Bearer " + Token, StringComparison.Ordinal)
            || header.StartsWith("Bearer " + Token + ":", StringComparison.Ordinal);
    }

    private static bool IsSecondAccount(HttpContext context)
        => context.Request.Headers.Authorization.ToString()
            .EndsWith(":" + SecondAccountUsername, StringComparison.Ordinal);

    private static async Task<P.RpcRequest> ReadAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        buffer.Position = 0;
        return P.RpcRequest.Parser.ParseFrom(buffer);
    }

    private static async Task WriteAsync(HttpContext context, P.RpcResponse response)
    {
        context.Response.ContentType = BackendClient.ProtobufMediaType;
        await context.Response.Body.WriteAsync(response.ToByteArray());
    }

    private static async Task WriteError(HttpContext context, int status, string code, string requestId)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = BackendClient.ProtobufMediaType;
        var response = new P.RpcResponse
        {
            ProtocolMajor = 1,
            RequestId = requestId,
            Error = new P.RpcError { Code = code, Message = code, HttpStatus = (uint)status },
        };
        await context.Response.Body.WriteAsync(response.ToByteArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
