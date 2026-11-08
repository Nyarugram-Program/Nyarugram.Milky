using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Nyarugram.Milky.Host;

public sealed record AdminLogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stream_id")] string StreamId,
    [property: JsonPropertyName("stream_started_at")] DateTimeOffset StreamStartedAt);

/// <summary>A bounded, redacted process log. It never reads deployment files.</summary>
public sealed partial class AdminLogBuffer : ILoggerProvider
{
    private readonly int _capacity;
    private readonly Queue<AdminLogEntry> _entries = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, AdminLogSubscription> _subscribers = new();
    private long _sequence;
    private long _dropped;

    public AdminLogBuffer(MilkyOptions options)
    {
        _capacity = Math.Clamp(options.ManagementLogCapacity, 16, 5000);
        StreamId = Guid.NewGuid().ToString("N");
        StreamStartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Identity of this process's log sequence. Entry ids restart at 1 after a
    /// restart, so clients must deduplicate on stream id + entry id.
    /// </summary>
    public string StreamId { get; }
    public DateTimeOffset StreamStartedAt { get; }

    public int Capacity => _capacity;
    public int SubscriberCount => _subscribers.Count;
    public long Dropped { get { lock (_gate) return _dropped; } }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        foreach (var subscriber in _subscribers.Values) subscriber.Dispose();
        _subscribers.Clear();
    }

    public IReadOnlyList<AdminLogEntry> Read(long after, LogLevel minimumLevel, int limit)
    {
        lock (_gate)
            return _entries.Where(entry => long.Parse(entry.Id) > after && LevelValue(entry.Level) >= (int)minimumLevel)
                .Take(limit).ToList();
    }

    /// <summary>Newest entries first-captured order, used for an initial page load.</summary>
    public IReadOnlyList<AdminLogEntry> ReadLatest(LogLevel minimumLevel, int limit)
    {
        lock (_gate)
            return _entries.Where(entry => LevelValue(entry.Level) >= (int)minimumLevel)
                .TakeLast(limit).ToList();
    }

    public AdminLogSubscription Subscribe(LogLevel minimumLevel)
    {
        var subscription = new AdminLogSubscription(this, minimumLevel);
        _subscribers[subscription.Id] = subscription;
        return subscription;
    }

    private void Write<TState>(
        string category, LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (exception is not null) text += $" [{exception.GetType().Name}]";
        text = AdminLogRedactor.Redact(text);
        if (text.Length > 4096) text = text[..4096] + "…";
        var entry = new AdminLogEntry(
            Interlocked.Increment(ref _sequence).ToString(),
            DateTimeOffset.UtcNow,
            level.ToString(),
            category,
            text,
            StreamId,
            StreamStartedAt);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
                _dropped++;
            }
        }
        foreach (var subscriber in _subscribers.Values) subscriber.Write(entry);
    }

    private void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    private static int LevelValue(string level) => level switch
    {
        nameof(LogLevel.Trace) => 0,
        nameof(LogLevel.Debug) => 1,
        nameof(LogLevel.Information) => 2,
        nameof(LogLevel.Warning) => 3,
        nameof(LogLevel.Error) => 4,
        nameof(LogLevel.Critical) => 5,
        _ => 6,
    };

    private sealed class Logger(AdminLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(category, logLevel, eventId, state, exception, formatter);
        }
    }

    public sealed class AdminLogSubscription : IDisposable
    {
        private readonly AdminLogBuffer _owner;
        private readonly LogLevel _minimumLevel;
        private readonly Channel<AdminLogEntry> _channel = Channel.CreateBounded<AdminLogEntry>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        private int _disposed;
        private long _dropped;

        internal AdminLogSubscription(AdminLogBuffer owner, LogLevel minimumLevel)
        {
            _owner = owner;
            _minimumLevel = minimumLevel;
            Id = Guid.NewGuid();
        }

        public Guid Id { get; }
        public ChannelReader<AdminLogEntry> Reader => _channel.Reader;
        public long Dropped => Interlocked.Read(ref _dropped);

        internal void Write(AdminLogEntry entry)
        {
            if (_disposed != 0 || LevelValue(entry.Level) < (int)_minimumLevel) return;
            if (!_channel.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Unsubscribe(Id);
            _channel.Writer.TryComplete();
        }
    }

    private sealed class NullScope : IDisposable { public void Dispose() { } }
}

public static partial class AdminLogRedactor
{
    private static readonly Regex SecretAssignment = new(
        "(?i)([\\\"']?(?:password|passwd|pwd|totp|access_token|refresh_token|authorization|secret_key|connection_string|private_key|cookie|token|bearer)[\\\"']?\\s*[:=]\\s*)(\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;}&]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerValue = new(
        "(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserInfo = new(
        "://[^/\\s:@]+:[^/\\s@]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKey = new(
        "-----BEGIN[^-]*PRIVATE KEY-----.*?-----END[^-]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static string Redact(string value)
    {
        if (value.Length == 0) return value;
        value = PrivateKey.Replace(value, "[redacted private key]");
        value = SecretAssignment.Replace(value, "$1[redacted]");
        value = BearerValue.Replace(value, "Bearer [redacted]");
        return UserInfo.Replace(value, "://[redacted]@");
    }
}
