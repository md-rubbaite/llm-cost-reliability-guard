using LlmGuard;

namespace LlmGuard.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
}

/// <summary>Scripted provider: each call takes the next behaviour from the queue (or the default).</summary>
internal sealed class FakeProvider(string name) : ILlmProvider
{
    private readonly Queue<Func<ModelSpec, LlmRequest, CancellationToken, Task<LlmResponse>>> _script = new();

    public string Name { get; } = name;

    public int Calls { get; private set; }

    public Func<ModelSpec, LlmRequest, CancellationToken, Task<LlmResponse>> Default { get; set; } =
        (m, r, _) => Task.FromResult(new LlmResponse($"{m.Model}: ok", 100, 50));

    public FakeProvider Then(Func<ModelSpec, LlmRequest, CancellationToken, Task<LlmResponse>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public FakeProvider ThenTransient(TimeSpan? retryAfter = null) =>
        Then((_, _, _) => throw new TransientLlmException("429 Too Many Requests") { RetryAfter = retryAfter });

    public FakeProvider ThenFatal() => Then((_, _, _) => throw new InvalidOperationException("400 content filtered"));

    public FakeProvider ThenHang() => Then(async (_, _, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new LlmResponse("never", 0, 0);
    });

    public Task<LlmResponse> CompleteAsync(ModelSpec model, LlmRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var step = _script.Count > 0 ? _script.Dequeue() : Default;
        return step(model, request, cancellationToken);
    }
}

public class GatewayTests
{
    private static readonly ModelSpec Mini = new("openai", "gpt-mini", ModelTier.Small, 0.15m, 0.60m, 128_000);
    private static readonly ModelSpec Haiku = new("anthropic", "claude-small", ModelTier.Small, 0.80m, 4.00m, 200_000);
    private static readonly ModelSpec Big = new("openai", "gpt-large", ModelTier.Large, 2.50m, 10.00m, 128_000);
    private static readonly ModelSpec Sonnet = new("anthropic", "claude-large", ModelTier.Large, 3.00m, 15.00m, 200_000);

    private readonly FakeClock _clock = new();
    private readonly FakeProvider _openai = new("openai");
    private readonly FakeProvider _anthropic = new("anthropic");
    private readonly List<TimeSpan> _delays = [];

    private LlmGateway Gateway(decimal budget = 10m, ResponseCache? cache = null, TimeSpan? timeout = null, int retries = 3) =>
        new(
            [_openai, _anthropic],
            new ModelRouter([Mini, Haiku, Big, Sonnet]),
            new BudgetLedger(new Dictionary<string, decimal> { ["acme"] = budget }, clock: _clock),
            cache,
            new RetryPolicy(retries),
            timeout ?? TimeSpan.FromSeconds(5),
            _ => new CircuitBreaker(3, TimeSpan.FromMinutes(1), _clock),
            _clock,
            (d, _) =>
            {
                _delays.Add(d);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Simple_prompts_use_the_cheapest_small_model_and_cost_is_exact()
    {
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", "Summarise this ticket in one line."));
        Assert.Equal("gpt-mini", r.Model);
        Assert.Equal(ModelTier.Small, r.Tier);
        Assert.Equal((100 * 0.15m + 50 * 0.60m) / 1_000_000m, r.CostUsd);
        Assert.Equal(1, r.Attempts);
    }

    [Theory]
    [InlineData("Compare these two architectures and recommend one.")]
    [InlineData("Explain step by step why this proof works.")]
    public async Task Complex_prompts_route_to_large_models(string prompt)
    {
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", prompt));
        Assert.Equal(ModelTier.Large, r.Tier);
        Assert.Equal("gpt-large", r.Model);
    }

    [Fact]
    public async Task Explicit_tier_overrides_the_heuristic()
    {
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", "Analyze this.", Tier: ModelTier.Small));
        Assert.Equal(ModelTier.Small, r.Tier);
    }

    [Fact]
    public async Task Transient_errors_are_retried_with_backoff_and_retry_after()
    {
        _openai.ThenTransient().ThenTransient(TimeSpan.FromSeconds(2));
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", "hello"));
        Assert.Equal("gpt-mini", r.Model);
        Assert.Equal(3, r.Attempts);
        Assert.Equal(2, _delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), _delays[1]); // Retry-After honoured
    }

    [Fact]
    public async Task Provider_outage_falls_back_to_the_next_provider()
    {
        _openai.ThenTransient().ThenTransient().ThenTransient();
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", "hello"));
        Assert.Equal("anthropic", r.Provider);
        Assert.Equal("claude-small", r.Model);
        Assert.Contains(r.Fallbacks, f => f.StartsWith("openai/gpt-mini", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_retryable_errors_skip_straight_to_the_next_model()
    {
        _openai.ThenFatal();
        var r = await Gateway().CompleteAsync(new LlmRequest("acme", "hello"));
        Assert.Equal("anthropic", r.Provider);
        Assert.Equal(1, _openai.Calls);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Timeouts_count_as_transient_failures()
    {
        _openai.ThenHang();
        var r = await Gateway(timeout: TimeSpan.FromMilliseconds(50)).CompleteAsync(new LlmRequest("acme", "hello"));
        Assert.Equal("gpt-mini", r.Model);
        Assert.Equal(2, r.Attempts);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_skips_the_provider()
    {
        var gw = Gateway(retries: 1);
        _openai.Default = (_, _, _) => throw new TransientLlmException("503");
        for (var i = 0; i < 3; i++)
        {
            await gw.CompleteAsync(new LlmRequest("acme", $"q{i}", AllowCache: false));
        }

        Assert.Equal(CircuitState.Open, gw.BreakerState("openai"));
        var callsBefore = _openai.Calls;
        var r = await gw.CompleteAsync(new LlmRequest("acme", "q-next", AllowCache: false));
        Assert.Equal(callsBefore, _openai.Calls); // not called while open
        Assert.Contains(r.Fallbacks, f => f.Contains("circuit open", StringComparison.Ordinal));

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        _openai.Default = (m, _, _) => Task.FromResult(new LlmResponse("recovered", 10, 10));
        var after = await gw.CompleteAsync(new LlmRequest("acme", "q-after", AllowCache: false));
        Assert.Equal("openai", after.Provider);
        Assert.Equal(CircuitState.Closed, gw.BreakerState("openai"));
    }

    [Fact]
    public async Task All_providers_failing_releases_the_budget_reservation()
    {
        _openai.Default = (_, _, _) => throw new TransientLlmException("503");
        _anthropic.Default = (_, _, _) => throw new TransientLlmException("529");
        var ledger = new BudgetLedger(new Dictionary<string, decimal> { ["acme"] = 1m }, clock: _clock);
        var gw = new LlmGateway([_openai, _anthropic], new ModelRouter([Mini, Haiku]), ledger, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<AllProvidersFailedException>(() => gw.CompleteAsync(new LlmRequest("acme", "hello")));
        Assert.Equal(1m, ledger.Remaining("acme"));
    }

    [Fact]
    public async Task Cache_hits_are_free_and_do_not_call_the_provider()
    {
        var gw = Gateway(cache: new ResponseCache(100, TimeSpan.FromHours(1), _clock));
        var first = await gw.CompleteAsync(new LlmRequest("acme", "What is our refund policy?"));
        var second = await gw.CompleteAsync(new LlmRequest("acme", "  what is our   REFUND policy? "));
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(0m, second.CostUsd);
        Assert.Equal(1, _openai.Calls);
    }

    [Fact]
    public async Task Cache_is_isolated_per_tenant_and_expires()
    {
        var cache = new ResponseCache(100, TimeSpan.FromMinutes(5), _clock);
        var gw = new LlmGateway(
            [_openai],
            new ModelRouter([Mini]),
            new BudgetLedger(new Dictionary<string, decimal> { ["a"] = 1m, ["b"] = 1m }, clock: _clock),
            cache,
            clock: _clock);
        await gw.CompleteAsync(new LlmRequest("a", "same question"));
        Assert.False((await gw.CompleteAsync(new LlmRequest("b", "same question"))).FromCache);
        _clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.False((await gw.CompleteAsync(new LlmRequest("a", "same question"))).FromCache);
    }

    [Fact]
    public async Task Budget_is_enforced_before_calling_the_model()
    {
        var gw = Gateway(budget: 0.0001m);
        await Assert.ThrowsAsync<BudgetExceededException>(
            () => gw.CompleteAsync(new LlmRequest("acme", "Write a long report.", MaxOutputTokens: 4_000, Tier: ModelTier.Large)));
        Assert.Equal(0, _openai.Calls);
    }

    [Fact]
    public async Task Unknown_tenant_is_rejected()
    {
        await Assert.ThrowsAsync<BudgetExceededException>(() => Gateway().CompleteAsync(new LlmRequest("mallory", "hi")));
    }

    [Fact]
    public async Task Prompts_too_large_for_every_model_fail_fast()
    {
        var huge = new string('x', 900_000);
        await Assert.ThrowsAsync<AllProvidersFailedException>(() => Gateway().CompleteAsync(new LlmRequest("acme", huge)));
        Assert.Equal(0, _openai.Calls + _anthropic.Calls);
    }

    [Fact]
    public async Task Usage_report_aggregates_cost_per_tenant_and_model()
    {
        var gw = Gateway(cache: new ResponseCache(10, TimeSpan.FromHours(1), _clock));
        await gw.CompleteAsync(new LlmRequest("acme", "a"));
        await gw.CompleteAsync(new LlmRequest("acme", "a"));
        await gw.CompleteAsync(new LlmRequest("acme", "Compare plans"));
        var report = gw.UsageReport();
        Assert.Contains("| acme | openai | gpt-mini | 2 | 1 | 200 | 100 |", report, StringComparison.Ordinal);
        Assert.Contains("gpt-large", report, StringComparison.Ordinal);
        Assert.Equal(3, gw.Usage.Count);
    }
}
