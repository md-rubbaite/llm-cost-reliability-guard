using System.Security.Cryptography;
using System.Text;

namespace LlmGuard;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>
/// Stops calling a provider that keeps failing, then lets one trial call through after a cool-down.
/// Prevents a provider outage from turning into timeouts for every user and wasted retries.
/// </summary>
public sealed class CircuitBreaker(int failureThreshold, TimeSpan coolDown, IClock? clock = null)
{
    private readonly object _gate = new();
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _trialInFlight;

    public CircuitState State { get; private set; } = CircuitState.Closed;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (State == CircuitState.Open && _clock.UtcNow - _openedAt >= coolDown)
            {
                State = CircuitState.HalfOpen;
                _trialInFlight = false;
            }

            switch (State)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.HalfOpen when !_trialInFlight:
                    _trialInFlight = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _trialInFlight = false;
            State = CircuitState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures++;
            if (State == CircuitState.HalfOpen || _failures >= failureThreshold)
            {
                State = CircuitState.Open;
                _openedAt = _clock.UtcNow;
                _trialInFlight = false;
            }
        }
    }
}

/// <summary>Exponential backoff with full jitter, honouring Retry-After when the provider sends it.</summary>
public sealed class RetryPolicy(int maxAttempts = 3, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
{
    private readonly TimeSpan _base = baseDelay ?? TimeSpan.FromMilliseconds(200);
    private readonly TimeSpan _max = maxDelay ?? TimeSpan.FromSeconds(8);

    public int MaxAttempts { get; } = maxAttempts < 1 ? throw new ArgumentOutOfRangeException(nameof(maxAttempts)) : maxAttempts;

    public TimeSpan DelayFor(int attempt, TimeSpan? retryAfter, Func<double>? random = null)
    {
        if (retryAfter is { } ra)
        {
            return ra > _max ? _max : ra;
        }

        var cap = Math.Min(_max.TotalMilliseconds, _base.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var r = random?.Invoke() ?? (RandomNumberGenerator.GetInt32(0, 10_000) / 10_000.0);
        return TimeSpan.FromMilliseconds(cap * r);
    }
}

/// <summary>
/// Exact-match response cache keyed by tenant, tier and a normalised prompt hash. Bounded (LRU) and time-limited.
/// Tenants never share entries. Semantic (embedding) caching is deliberately out of scope: it needs an
/// embedding model and a similarity threshold that must be validated per use case.
/// </summary>
public sealed class ResponseCache(int capacity, TimeSpan ttl, IClock? clock = null)
{
    private readonly object _gate = new();
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly LinkedList<(string Key, GuardedResponse Value, DateTimeOffset Expires)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, GuardedResponse Value, DateTimeOffset Expires)>> _map = new(StringComparer.Ordinal);

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public static string Key(string tenant, ModelTier tier, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var normalised = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
        return $"{tenant}|{tier}|{hash}";
    }

    public bool TryGet(string key, out GuardedResponse? value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node) && node.Value.Expires > _clock.UtcNow)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                Hits++;
                value = node.Value.Value;
                return true;
            }

            if (node is not null)
            {
                _lru.Remove(node);
                _map.Remove(key);
            }

            Misses++;
            value = null;
            return false;
        }
    }

    public void Set(string key, GuardedResponse value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
            }

            var node = _lru.AddFirst((key, value, _clock.UtcNow + ttl));
            _map[key] = node;
            while (_map.Count > capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
