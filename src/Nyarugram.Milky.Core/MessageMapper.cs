using System.Text;
using Microsoft.Extensions.Logging;
using Nyarugram.Milky.Contracts;
using Nyarugram.Milky.Storage;
using P = Botgram.Protocol.V1;

namespace Nyarugram.Milky.Core;

public sealed record OutgoingComposition(string Text, IReadOnlyList<P.MessageEntity> Entities, long? ReplyToMessageId);

/// <summary>
/// Converts between backend messages and Milky message segments. Only segments with a
/// real Nyarugram representation are produced; everything else is rejected as
/// unsupported instead of being silently dropped or faked.
/// </summary>
public sealed class MessageMapper
{
    private const int MaxTextLength = 16000;
    private const int MaxEntities = 100;

    private readonly BackendRpc _rpc;
    private readonly IdentityResolver _identities;
    private readonly GroupMemberDirectory _members;
    private readonly ILogger<MessageMapper> _logger;

    public MessageMapper(
        BackendRpc rpc, IdentityResolver identities, GroupMemberDirectory members, ILogger<MessageMapper> logger)
    {
        _rpc = rpc;
        _identities = identities;
        _members = members;
        _logger = logger;
    }

    // --------------------------------------------------------------- inbound

    public async Task<IncomingMessage> ToMilkyAsync(
        SessionContext context, P.Message message, CancellationToken ct)
    {
        var senderUin = message.Sender is not null
            ? await _identities.RequireUinForUserAsync(context, message.Sender.UserId, ct).ConfigureAwait(false)
            : context.Account.Uin;
        var time = message.CreatedAtMs / 1000;

        if (message.Peer?.PeerCase == P.PeerRef.PeerOneofCase.GroupId)
        {
            var groupUin = await _identities
                .RequireUinForGroupAsync(context, message.Peer.GroupId, ct).ConfigureAwait(false);
            var group = await _identities.RequireGroupAsync(context, groupUin, ct).ConfigureAwait(false);
            var member = await _members.GetAsync(context, groupUin, senderUin, ct).ConfigureAwait(false);
            return new GroupMessage
            {
                PeerId = groupUin,
                MessageSeq = message.MessageId,
                SenderId = senderUin,
                Time = time,
                Segments = await BuildSegmentsAsync(context, message, ct).ConfigureAwait(false),
                Group = MapGroup(group),
                GroupMember = member,
            };
        }

        var peerUin = message.Peer?.PeerCase switch
        {
            P.PeerRef.PeerOneofCase.User => await _identities
                .RequireUinForUserAsync(context, message.Peer.User.UserId, ct).ConfigureAwait(false),
            P.PeerRef.PeerOneofCase.Self => context.Account.Uin,
            _ => context.Account.Uin,
        };
        var friend = await _identities.FindUserAsync(context, peerUin, ct).ConfigureAwait(false);
        return new FriendMessage
        {
            PeerId = peerUin,
            MessageSeq = message.MessageId,
            SenderId = senderUin,
            Time = time,
            Segments = await BuildSegmentsAsync(context, message, ct).ConfigureAwait(false),
            Friend = MapFriend(friend, peerUin),
        };
    }

    private async Task<List<MessageSegment>> BuildSegmentsAsync(
        SessionContext context, P.Message message, CancellationToken ct)
    {
        var segments = new List<MessageSegment>();
        switch (message.BodyCase)
        {
            case P.Message.BodyOneofCase.Text:
                await AppendTextWithEntitiesAsync(context, segments, message.Text, ct).ConfigureAwait(false);
                if (message.Text.Reply is not null)
                {
                    var reply = await BuildReplyAsync(context, message.Text.Reply.MessageId, ct).ConfigureAwait(false);
                    if (reply is not null) segments.Insert(0, reply);
                }
                break;
            case P.Message.BodyOneofCase.Service:
                // Service messages have no Milky segment; expose the text the backend gave.
                segments.Add(MessageSegment.Text(message.Service.ActionCase switch
                {
                    P.ServiceMessage.ActionOneofCase.GroupCreated => $"群组已创建：{message.Service.GroupCreated.Title}",
                    P.ServiceMessage.ActionOneofCase.E2EeSystem => message.Service.E2EeSystem.Text,
                    _ => "",
                }));
                break;
            case P.Message.BodyOneofCase.E2Ee:
                throw new MilkyActionException(MilkyErrorCode.Unsupported,
                    "e2ee content is never decrypted and has no Milky segment representation");
            default:
                segments.Add(MessageSegment.Text(""));
                break;
        }
        return segments;
    }

    /// <summary>
    /// Splits backend text into text/mention segments using the stored UTF-16 entities.
    /// A mention entity carries only "@name"; the numeric id comes from the backend
    /// username lookup and is cached. An unresolvable mention stays plain text instead
    /// of inventing an id.
    /// </summary>
    private async Task AppendTextWithEntitiesAsync(
        SessionContext context, List<MessageSegment> segments, P.TextMessage text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            segments.Add(MessageSegment.Text(""));
            return;
        }
        var cursor = 0;
        foreach (var entity in text.Entities.OrderBy(e => e.OffsetUtf16))
        {
            if (entity.KindCase != P.MessageEntity.KindOneofCase.Mention) continue;
            var start = (int)entity.OffsetUtf16;
            var length = (int)entity.LengthUtf16;
            if (start < cursor || start + length > text.Text.Length) continue;

            var mentionText = text.Text.Substring(start, length);
            if (!TryParseUsername(mentionText, out var username)) continue;

            var resolved = await _identities.FindByUsernameAsync(context, username, ct).ConfigureAwait(false);
            if (start > cursor) segments.Add(MessageSegment.Text(text.Text[cursor..start]));
            if (resolved is { TlUserId: > 0 })
                segments.Add(MessageSegment.Mention(resolved.TlUserId));
            else
                segments.Add(MessageSegment.Text(mentionText));
            cursor = start + length;
        }
        if (cursor < text.Text.Length) segments.Add(MessageSegment.Text(text.Text[cursor..]));
    }

    private static bool TryParseUsername(string mention, out string username)
    {
        if (mention.StartsWith('@') && mention.Length > 1 && mention.Length <= 33)
        {
            username = mention[1..];
            return username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        }
        username = "";
        return false;
    }

    private async Task<MessageSegment?> BuildReplyAsync(SessionContext context, long messageId, CancellationToken ct)
    {
        try
        {
            var response = await _rpc.CallAsync(context, new P.RpcRequest
            {
                GetMessages = new P.GetMessagesRequest { MessageIds = { messageId } },
            }, ct).ConfigureAwait(false);
            var referenced = response.GetMessages?.Messages.FirstOrDefault();
            if (referenced is null) return null;
            var senderUin = referenced.Sender is not null
                ? await _identities.RequireUinForUserAsync(context, referenced.Sender.UserId, ct).ConfigureAwait(false)
                : context.Account.Uin;
            var data = new Dictionary<string, object?>
            {
                ["message_seq"] = referenced.MessageId,
                ["sender_id"] = senderUin,
                ["time"] = referenced.CreatedAtMs / 1000,
                ["segments"] = new[] { new { type = "text", data = new { text = ReferencedText(referenced) } } },
            };
            return new MessageSegment
            {
                Type = "reply",
                Data = System.Text.Json.JsonSerializer.SerializeToElement(data, MilkyJson.Options),
            };
        }
        catch (MilkyActionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Backend.BackendException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "reply source {MessageId} unavailable", messageId);
            return null;
        }
    }

    private static string ReferencedText(P.Message message) => message.BodyCase switch
    {
        P.Message.BodyOneofCase.Text => message.Text.Text,
        P.Message.BodyOneofCase.Service => "",
        _ => "",
    };

    public static FriendEntity MapFriend(P.UserSummary? user, long uin) => new()
    {
        UserId = uin,
        Nickname = user is null ? "" : DisplayName(user),
        Remark = "",
        Category = new FriendCategoryEntity { CategoryId = 0, CategoryName = "默认" },
    };

    public static GroupEntity MapGroup(P.GroupSummary group) => new()
    {
        GroupId = group.TlChatId,
        GroupName = group.Title,
        MemberCount = group.ParticipantCount,
        MaxMemberCount = 0,
        Remark = "",
        CreatedTime = group.CreatedAtMs / 1000,
    };

    public static string DisplayName(P.UserSummary user)
        => !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
            : !string.IsNullOrEmpty(user.Username) ? user.Username
            : uin(user);

    private static string uin(P.UserSummary user) => user.TlUserId.ToString();

    // --------------------------------------------------------------- outbound

    public async Task<OutgoingComposition> ComposeAsync(
        SessionContext context, IReadOnlyList<MessageSegment> segments, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var entities = new List<P.MessageEntity>();
        long? replyTo = null;
        var replySeen = false;

        foreach (var segment in segments)
        {
            switch (segment.Type)
            {
                case "text":
                {
                    var text = segment.Data.TryGetProperty("text", out var value) ? value.GetString() : null;
                    if (text is null)
                        throw new MilkyActionException(MilkyErrorCode.BadRequest, "text segment requires string field text");
                    builder.Append(text);
                    break;
                }
                case "mention":
                {
                    if (segment.Data.TryGetProperty("user_id", out var value) && value.TryGetInt64(out var uin))
                    {
                        var user = await _identities.RequireUserAsync(context, uin, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(user.Username))
                            throw new MilkyActionException(MilkyErrorCode.Unsupported,
                                $"user {uin} has no username; a native mention cannot be expressed");
                        var token = "@" + user.Username;
                        var offset = builder.Length;
                        builder.Append(token);
                        entities.Add(new P.MessageEntity
                        {
                            OffsetUtf16 = (uint)offset,
                            LengthUtf16 = (uint)token.Length,
                            Mention = new P.Empty(),
                        });
                    }
                    else
                    {
                        throw new MilkyActionException(MilkyErrorCode.BadRequest, "mention segment requires user_id");
                    }
                    break;
                }
                case "reply":
                {
                    if (replySeen)
                        throw new MilkyActionException(MilkyErrorCode.BadRequest, "only one reply segment is allowed");
                    if (!segment.Data.TryGetProperty("message_seq", out var value) || !value.TryGetInt64(out var messageId))
                        throw new MilkyActionException(MilkyErrorCode.BadRequest, "reply segment requires message_seq");
                    replyTo = messageId;
                    replySeen = true;
                    break;
                }
                case "mention_all":
                    throw new MilkyActionException(MilkyErrorCode.Unsupported,
                        "mention_all has no Nyarugram representation");
                default:
                    throw new MilkyActionException(MilkyErrorCode.Unsupported,
                        $"message segment '{segment.Type}' is not supported by this adapter");
            }
        }

        var textValue = builder.ToString();
        if (string.IsNullOrEmpty(textValue))
            throw new MilkyActionException(MilkyErrorCode.BadRequest, "message must contain text");
        if (textValue.Length > MaxTextLength)
            throw new MilkyActionException(MilkyErrorCode.BadRequest, $"message text exceeds {MaxTextLength} characters");
        if (entities.Count > MaxEntities)
            throw new MilkyActionException(MilkyErrorCode.BadRequest, $"message exceeds {MaxEntities} entities");

        return new OutgoingComposition(textValue, entities, replyTo);
    }
}
