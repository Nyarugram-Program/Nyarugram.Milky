using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Nyarugram.Milky.Core;

/// <summary>One durable event handed to a consumer, carrying its global sequence.</summary>
public readonly record struct EventEnvelope(long Seq, string EventId, string EventType, string Payload);

/// <summary>
/// Bounded fan-out to connected event consumers.
///
/// Every subscription is bound to one server/account/generation scope. Routing, the minimum
/// cursor used for delivery accounting and the persisted consumer cursor all use that scope,
/// so an old connection can never observe or acknowledge another account's events.
///
/// A consumer tracks two positions: what was written to the socket (sent) and what the client
/// explicitly acknowledged (acked). Only the acked position is persisted for consumers that
/// use the acknowledgement extension.
/// </summary>
public sealed class EventBus
{
    private readonly ILogger<EventBus> _logger;
    private readonly ConcurrentDictionary<Guid, EventSubscription> _subscriptions = new();

    public EventBus(ILogger<EventBus> logger) => _logger = logger;

    public int Count => _subscriptions.Count;

    public EventSubscription Subscribe(
        string kind, string consumerId, string scope, Guid accountId, long startSeq, int capacity = 512,
        long epoch = 0)
    {
        var subscription = new EventSubscription(
            Guid.NewGuid(), kind, consumerId, scope, accountId, startSeq, capacity, epoch, Unsubscribe, _logger);
        _subscriptions[subscription.Id] = subscription;
        _logger.LogInformation("event consumer {Kind}/{Consumer} connected at seq {Seq} scope={Scope}",
            kind, consumerId, startSeq, scope);
        return subscription;
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscriptions.TryRemove(id, out _))
            _logger.LogInformation("event consumer disconnected ({Count} remain)", _subscriptions.Count);
    }

    /// <summary>
    /// Drops every subscription of a scope. Called when the account logs out or a new session
    /// generation replaces the old one, so queued events are not delivered to a dead session.
    /// </summary>
    public int CancelScope(string scope)
    {
        var cancelled = 0;
        foreach (var subscription in _subscriptions.Values)
        {
            if (!string.Equals(subscription.Scope, scope, StringComparison.Ordinal)) continue;
            subscription.Cancel();
            Unsubscribe(subscription.Id);
            cancelled++;
        }
        if (cancelled > 0)
            _logger.LogInformation("cancelled {Count} event subscriptions for scope {Scope}", cancelled, scope);
        return cancelled;
    }

    /// <summary>Publishes one row to every consumer of that account that has not reached it.</summary>
    public int Publish(Guid accountId, EventEnvelope envelope)
    {
        var delivered = 0;
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.AccountId != accountId) continue;
            if (subscription.SentCursor >= envelope.Seq) continue;
            if (subscription.TryWrite(envelope)) delivered++;
        }
        return delivered;
    }

    /// <summary>Lowest acked cursor among connected consumers of an account, or -1.</summary>
    public long MinCursor(Guid accountId)
    {
        var min = long.MaxValue;
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.AccountId != accountId) continue;
            min = Math.Min(min, subscription.Cursor);
        }
        return min == long.MaxValue ? -1 : min;
    }

    /// <summary>
    /// Lowest sent (written) position among connected consumers. Unlike <see cref="MinCursor"/>,
    /// this advances even when a consumer never acknowledges, so one silent consumer cannot
    /// stall delivery of new events to everyone else.
    /// </summary>
    public long MinSentCursor(Guid accountId)
    {
        var min = long.MaxValue;
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.AccountId != accountId) continue;
            min = Math.Min(min, subscription.SentCursor);
        }
        return min == long.MaxValue ? -1 : min;
    }

    public IReadOnlyList<string> Consumers(Guid accountId)
        => _subscriptions.Values.Where(s => s.AccountId == accountId).Select(s => s.ConsumerId).ToList();
}

public sealed class EventSubscription : IAsyncEnumerable<EventEnvelope>, IDisposable
{
    private readonly Channel<EventEnvelope> _channel;
    private readonly Action<Guid> _unsubscribe;
    private readonly ILogger _logger;
    private long _acked;
    private long _sent;
    private long _epoch;
    private readonly CancellationTokenSource _stopped = new();
    private int _disposed;

    internal EventSubscription(
        Guid id, string kind, string consumerId, string scope, Guid accountId, long startSeq, int capacity,
        long epoch, Action<Guid> unsubscribe, ILogger logger)
    {
        Id = id;
        Kind = kind;
        ConsumerId = consumerId;
        Scope = scope;
        AccountId = accountId;
        _acked = startSeq;
        _sent = startSeq;
        _epoch = epoch;
        _unsubscribe = unsubscribe;
        _logger = logger;
        _channel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; }
    public string Kind { get; }
    public string ConsumerId { get; }
    public string Scope { get; }
    public Guid AccountId { get; }

    /// <summary>Highest sequence the consumer acknowledged.</summary>
    public long Cursor => Interlocked.Read(ref _acked);

    /// <summary>Highest sequence written to the socket.</summary>
    public long SentCursor => Interlocked.Read(ref _sent);

    /// <summary>Cursor generation. A rewind bumps it so stale writers are ignored.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Cancels blocked transport writes when the subscription is removed.</summary>
    public CancellationToken Stopped => _stopped.Token;

    /// <summary>Adopts a new cursor generation returned by a rewind.</summary>
    public void AdoptEpoch(long epoch) => Interlocked.Exchange(ref _epoch, epoch);

    /// <summary>Set when the consumer fell behind and was disconnected.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Marks a sequence as written. Called after a successful socket write.</summary>
    public void MarkSent(long seq)
    {
        var current = Interlocked.Read(ref _sent);
        if (seq > current) Interlocked.Exchange(ref _sent, seq);
    }

    /// <summary>
    /// Accepts an acknowledgement only when it is ordered and not ahead of what was sent.
    /// Duplicates are idempotent; an ahead-of-send ack is rejected so a buggy client cannot
    /// skip undelivered events.
    /// </summary>
    public bool TryAck(long seq, out string reason)
    {
        if (seq > SentCursor)
        {
            reason = $"ack {seq} is ahead of the last sent sequence {SentCursor}";
            return false;
        }
        var current = Interlocked.Read(ref _acked);
        if (seq <= current)
        {
            reason = "duplicate or older acknowledgement";
            return false;
        }
        Interlocked.Exchange(ref _acked, seq);
        reason = "";
        return true;
    }

    /// <summary>Explicit recovery: move the acked position back within what was already sent.</summary>
    public bool TryResume(long seq, out string reason)
    {
        if (seq < 0 || seq > SentCursor)
        {
            reason = $"resume cursor {seq} is outside the sent range";
            return false;
        }
        Interlocked.Exchange(ref _acked, seq);
        reason = "";
        return true;
    }

    /// <summary>
    /// Re-injects already-sent events into the delivery channel after an explicit resume.
    /// These bypass the normal <see cref="SentCursor"/> gate so a moved-back cursor actually
    /// redelivers, instead of only changing the durable position.
    /// </summary>
    public void Replay(IReadOnlyList<EventEnvelope> envelopes)
    {
        foreach (var envelope in envelopes)
        {
            if (!_channel.Writer.TryWrite(envelope))
            {
                Overflowed = true;
                Cancel();
                return;
            }
        }
    }

    internal bool TryWrite(EventEnvelope envelope)
    {
        if (_channel.Writer.TryWrite(envelope)) return true;
        Overflowed = true;
        _logger.LogWarning("event consumer {Consumer} overflowed; disconnecting", ConsumerId);
        Cancel();
        return false;
    }

    /// <summary>Ends the delivery loop and closes the channel.</summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Remove it before cancelling transport I/O. A blocked writer must no longer
        // participate in MinSentCursor even if cancellation takes time to unwind.
        _unsubscribe(Id);
        _channel.Writer.TryComplete();
        _stopped.Cancel();
    }

    public async IAsyncEnumerator<EventEnvelope> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public void Dispose()
    {
        Cancel();
    }
}
