using LlmGuard;

namespace LlmGuard.Tests;

public class BudgetTests
{
    private readonly FakeClock _clock = new();

    private BudgetLedger Ledger(decimal limit = 1m) => new(new Dictionary<string, decimal> { ["t"] = limit }, 0.8m, _clock);

    [Fact]
    public void Reservations_count_against_the_budget_until_settled()
    {
        var ledger = Ledger();
        using var a = ledger.Reserve("t", 0.6m);
        Assert.Throws<BudgetExceededException>(() => ledger.Reserve("t", 0.5m));
        a.Settle(0.1m);
        using var b = ledger.Reserve("t", 0.5m);
        Assert.Equal(0.1m, ledger.Spent("t"));
        Assert.Equal(0.4m, ledger.Remaining("t"));
    }

    [Fact]
    public void Disposing_without_settling_releases_the_hold()
    {
        var ledger = Ledger();
        using (ledger.Reserve("t", 0.9m))
        {
        }

        Assert.Equal(1m, ledger.Remaining("t"));
        Assert.Equal(0m, ledger.Spent("t"));
    }

    [Fact]
    public void Settling_twice_is_an_error()
    {
        using var r = Ledger().Reserve("t", 0.1m);
        r.Settle(0.05m);
        Assert.Throws<InvalidOperationException>(() => r.Settle(0.05m));
    }

    [Fact]
    public void Threshold_alert_fires_once_per_month()
    {
        var ledger = Ledger();
        var alerts = new List<decimal>();
        ledger.ThresholdCrossed += (_, spent, _) => alerts.Add(spent);
        ledger.Reserve("t", 0.5m).Settle(0.5m);
        ledger.Reserve("t", 0.35m).Settle(0.35m); // 0.85 >= 0.8
        ledger.Reserve("t", 0.05m).Settle(0.05m);
        Assert.Equal(new List<decimal> { 0.85m }, alerts);
    }

    [Fact]
    public void Budgets_reset_each_calendar_month()
    {
        var ledger = Ledger();
        ledger.Reserve("t", 1m).Settle(1m);
        Assert.Throws<BudgetExceededException>(() => ledger.Reserve("t", 0.01m));
        _clock.UtcNow = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        using var next = ledger.Reserve("t", 0.5m);
        Assert.Equal(0m, ledger.Spent("t"));
    }

    [Fact]
    public async Task Concurrent_reservations_never_overspend()
    {
        var ledger = Ledger(1m);
        var granted = 0;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            try
            {
                ledger.Reserve("t", 0.01m).Settle(0.01m);
                Interlocked.Increment(ref granted);
            }
            catch (BudgetExceededException)
            {
            }
        })));
        Assert.Equal(100, granted);
        Assert.Equal(1m, ledger.Spent("t"));
    }
}

public class CircuitBreakerTests
{
    [Fact]
    public void Opens_after_threshold_and_allows_one_trial_after_cool_down()
    {
        var clock = new FakeClock();
        var cb = new CircuitBreaker(2, TimeSpan.FromSeconds(30), clock);
        cb.RecordFailure();
        Assert.True(cb.TryAcquire());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.TryAcquire());

        clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.True(cb.TryAcquire());
        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.False(cb.TryAcquire()); // only one trial at a time

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.True(cb.TryAcquire());
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var p = new RetryPolicy(5, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), p.DelayFor(1, null, () => 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(400), p.DelayFor(3, null, () => 1.0));
        Assert.Equal(TimeSpan.FromSeconds(1), p.DelayFor(10, null, () => 1.0));
        Assert.Equal(TimeSpan.Zero, p.DelayFor(3, null, () => 0.0));
    }

    [Fact]
    public void Retry_after_is_used_but_capped()
    {
        var p = new RetryPolicy(3, maxDelay: TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayFor(1, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), p.DelayFor(1, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void At_least_one_attempt_is_required() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RetryPolicy(0));
}

public class CacheTests
{
    [Fact]
    public void Least_recently_used_entries_are_evicted()
    {
        var clock = new FakeClock();
        var cache = new ResponseCache(2, TimeSpan.FromHours(1), clock);
        var r = new GuardedResponse("x", "p", "m", ModelTier.Small, 1, 1, 0m, false, 1, []);
        cache.Set("a", r);
        cache.Set("b", r);
        Assert.True(cache.TryGet("a", out _)); // a is now most recent
        cache.Set("c", r);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(3, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Keys_normalise_whitespace_and_case_but_separate_tenants_and_tiers()
    {
        Assert.Equal(ResponseCache.Key("t", ModelTier.Small, "Hello   World"), ResponseCache.Key("t", ModelTier.Small, " hello world "));
        Assert.NotEqual(ResponseCache.Key("t", ModelTier.Small, "x"), ResponseCache.Key("u", ModelTier.Small, "x"));
        Assert.NotEqual(ResponseCache.Key("t", ModelTier.Small, "x"), ResponseCache.Key("t", ModelTier.Large, "x"));
    }
}

public class ModelTests
{
    [Fact]
    public void Cost_uses_per_million_token_prices()
    {
        var m = new ModelSpec("p", "m", ModelTier.Large, 3m, 15m, 200_000);
        Assert.Equal(0.0105m, m.Cost(1_000, 500));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void Token_estimate(string text, int expected) => Assert.Equal(expected, TokenEstimator.Estimate(text));

    [Fact]
    public void Router_requires_a_model() =>
        Assert.Throws<ArgumentException>(() => _ = new ModelRouter([]));

    [Fact]
    public void Long_prompts_route_to_large()
    {
        var router = new ModelRouter([new ModelSpec("p", "s", ModelTier.Small, 1, 1, 1_000_000)], largePromptTokens: 10);
        Assert.Equal(ModelTier.Large, router.DecideTier(new LlmRequest("t", new string('a', 100))));
        Assert.Equal(ModelTier.Small, router.DecideTier(new LlmRequest("t", "short")));
    }
}
