using System.Globalization;
using System.Text;

namespace LlmGuard;

/// <summary>Chooses the tier for a request and the ordered list of models to try.</summary>
public sealed class ModelRouter
{
    private readonly IReadOnlyList<ModelSpec> _catalog;
    private readonly int _largePromptTokens;
    private readonly string[] _complexMarkers;

    public ModelRouter(IReadOnlyList<ModelSpec> catalogInPreferenceOrder, int largePromptTokens = 2_000, IEnumerable<string>? complexMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(catalogInPreferenceOrder);
        if (catalogInPreferenceOrder.Count == 0)
        {
            throw new ArgumentException("The catalog must contain at least one model.", nameof(catalogInPreferenceOrder));
        }

        _catalog = catalogInPreferenceOrder;
        _largePromptTokens = largePromptTokens;
        _complexMarkers = (complexMarkers ?? ["step by step", "analyse", "analyze", "architecture", "contract", "```", "prove", "compare"]).ToArray();
    }

    /// <summary>Explicit tier wins; otherwise long or complex prompts go to Large, everything else to Small.</summary>
    public ModelTier DecideTier(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tier is { } tier)
        {
            return tier;
        }

        if (TokenEstimator.Estimate(request.Prompt) >= _largePromptTokens)
        {
            return ModelTier.Large;
        }

        return _complexMarkers.Any(m => request.Prompt.Contains(m, StringComparison.OrdinalIgnoreCase)) ? ModelTier.Large : ModelTier.Small;
    }

    /// <summary>Models of the tier that can fit the request, in preference order (the fallback chain).</summary>
    public IReadOnlyList<ModelSpec> Candidates(ModelTier tier, LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var needed = TokenEstimator.Estimate(request.Prompt) + request.MaxOutputTokens;
        return _catalog.Where(m => m.Tier == tier && m.ContextTokens >= needed).ToList();
    }
}

public sealed record UsageRecord(DateTimeOffset At, string Tenant, string Provider, string Model, int InputTokens, int OutputTokens, decimal CostUsd, bool FromCache);

/// <summary>
/// One entry point for every LLM call: cache → budget reservation → routing → retries, timeouts and circuit
/// breakers per provider → fallback to the next model → actual cost settled and recorded.
/// </summary>
public sealed class LlmGateway
{
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly ModelRouter _router;
    private readonly BudgetLedger _budget;
    private readonly ResponseCache? _cache;
    private readonly RetryPolicy _retry;
    private readonly TimeSpan _attemptTimeout;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, CircuitBreaker> _breakers;
    private readonly List<UsageRecord> _usage = [];
    private readonly object _usageGate = new();

    public LlmGateway(
        IEnumerable<ILlmProvider> providers,
        ModelRouter router,
        BudgetLedger budget,
        ResponseCache? cache = null,
        RetryPolicy? retry = null,
        TimeSpan? attemptTimeout = null,
        Func<string, CircuitBreaker>? breakerFactory = null,
        IClock? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _cache = cache;
        _retry = retry ?? new RetryPolicy();
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? SystemClock.Instance;
        _delay = delay ?? Task.Delay;
        breakerFactory ??= _ => new CircuitBreaker(5, TimeSpan.FromSeconds(30), _clock);
        _breakers = _providers.Keys.ToDictionary(n => n, breakerFactory, StringComparer.Ordinal);
    }

    public CircuitState BreakerState(string provider) => _breakers[provider].State;

    public IReadOnlyList<UsageRecord> Usage
    {
        get
        {
            lock (_usageGate)
            {
                return [.. _usage];
            }
        }
    }

    public async Task<GuardedResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tier = _router.DecideTier(request);
        var cacheKey = ResponseCache.Key(request.Tenant, tier, request.Prompt);
        if (request.AllowCache && _cache is not null && _cache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            var hit = cached with { FromCache = true, CostUsd = 0m, Attempts = 0, Fallbacks = [] };
            Record(request.Tenant, hit);
            return hit;
        }

        var candidates = _router.Candidates(tier, request);
        if (candidates.Count == 0)
        {
            throw new AllProvidersFailedException($"No {tier} model can fit a prompt of ~{TokenEstimator.Estimate(request.Prompt)} tokens plus {request.MaxOutputTokens} output tokens.");
        }

        var estimatedInput = TokenEstimator.Estimate(request.Prompt);
        var worstCase = candidates.Max(m => m.Cost(estimatedInput, request.MaxOutputTokens));
        using var reservation = _budget.Reserve(request.Tenant, worstCase);

        var fallbacks = new List<string>();
        var attempts = 0;
        foreach (var model in candidates)
        {
            if (!_providers.TryGetValue(model.Provider, out var provider))
            {
                fallbacks.Add($"{model.Provider}/{model.Model}: provider not registered");
                continue;
            }

            var breaker = _breakers[provider.Name];
            for (var attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
            {
                if (!breaker.TryAcquire())
                {
                    fallbacks.Add($"{provider.Name}/{model.Model}: circuit open");
                    break;
                }

                attempts++;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_attemptTimeout);
                try
                {
                    var response = await provider.CompleteAsync(model, request, timeout.Token).ConfigureAwait(false);
                    breaker.RecordSuccess();
                    var cost = model.Cost(response.InputTokens, response.OutputTokens);
                    reservation.Settle(cost);
                    var result = new GuardedResponse(response.Text, provider.Name, model.Model, tier, response.InputTokens, response.OutputTokens, cost, false, attempts, fallbacks);
                    Record(request.Tenant, result);
                    if (request.AllowCache)
                    {
                        _cache?.Set(cacheKey, result);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    breaker.RecordFailure();
                    if (!await RetryOrGiveUp(provider, model, attempt, null, "timeout", fallbacks, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (TransientLlmException ex)
                {
                    breaker.RecordFailure();
                    if (!await RetryOrGiveUp(provider, model, attempt, ex.RetryAfter, ex.Message, fallbacks, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not retryable (bad request, auth, content filter): try the next model, do not hammer this one.
                    breaker.RecordFailure();
                    fallbacks.Add($"{provider.Name}/{model.Model}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }
        }

        throw new AllProvidersFailedException($"All {candidates.Count} {tier} models failed: {string.Join("; ", fallbacks)}");
    }

    private async Task<bool> RetryOrGiveUp(ILlmProvider provider, ModelSpec model, int attempt, TimeSpan? retryAfter, string reason, List<string> fallbacks, CancellationToken ct)
    {
        if (attempt >= _retry.MaxAttempts)
        {
            fallbacks.Add($"{provider.Name}/{model.Model}: {reason} after {attempt} attempts");
            return false;
        }

        await _delay(_retry.DelayFor(attempt, retryAfter), ct).ConfigureAwait(false);
        return true;
    }

    private void Record(string tenant, GuardedResponse r)
    {
        lock (_usageGate)
        {
            _usage.Add(new UsageRecord(_clock.UtcNow, tenant, r.Provider, r.Model, r.InputTokens, r.OutputTokens, r.CostUsd, r.FromCache));
        }
    }

    /// <summary>Cost report per tenant and model, as a Markdown table.</summary>
    public string UsageReport()
    {
        var rows = Usage
            .GroupBy(u => (u.Tenant, u.Provider, u.Model))
            .OrderBy(g => g.Key.Tenant, StringComparer.Ordinal).ThenByDescending(g => g.Sum(u => u.CostUsd))
            .Select(g => (g.Key, Calls: g.Count(), Cached: g.Count(u => u.FromCache), In: g.Sum(u => u.InputTokens), Out: g.Sum(u => u.OutputTokens), Cost: g.Sum(u => u.CostUsd)));
        var sb = new StringBuilder();
        sb.AppendLine("| Tenant | Provider | Model | Calls | Cache hits | Input tokens | Output tokens | Cost (USD) |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Key.Tenant} | {r.Key.Provider} | {r.Key.Model} | {r.Calls} | {r.Cached} | {r.In} | {r.Out} | {r.Cost:0.000000} |");
        }

        return sb.ToString();
    }
}
