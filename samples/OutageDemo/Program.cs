using LlmGuard;

// Simulates a busy day: a provider rate-limits, then goes down, while two tenants send traffic.
// Prices are illustrative placeholders, not quotes from any provider.
var catalog = new List<ModelSpec>
{
    new("primary", "small-model", ModelTier.Small, 0.15m, 0.60m, 128_000),
    new("secondary", "small-model-b", ModelTier.Small, 0.25m, 1.25m, 200_000),
    new("primary", "large-model", ModelTier.Large, 2.50m, 10.00m, 128_000),
    new("secondary", "large-model-b", ModelTier.Large, 3.00m, 15.00m, 200_000),
};

var primary = new SimulatedProvider("primary", failFromCall: 6);
var secondary = new SimulatedProvider("secondary", failFromCall: int.MaxValue);
var budget = new BudgetLedger(new Dictionary<string, decimal> { ["retail"] = 0.005m, ["support"] = 0.006m });
budget.ThresholdCrossed += (tenant, spent, limit) => Console.WriteLine($"  ALERT {tenant}: ${spent:0.0000} of ${limit:0.00} used");

var gateway = new LlmGateway(
    [primary, secondary],
    new ModelRouter(catalog),
    budget,
    new ResponseCache(1_000, TimeSpan.FromMinutes(30)),
    new RetryPolicy(2, TimeSpan.FromMilliseconds(10)),
    TimeSpan.FromSeconds(2),
    _ => new CircuitBreaker(3, TimeSpan.FromMinutes(1)));

string[] prompts =
[
    "Where is my order 1042?",
    "Where is my order 1042?",
    "Compare the premium and basic plans for a family of four.",
    "Reset my password",
    "Summarise this chat for the agent.",
    "Where is my order 1042?",
    "Explain step by step how to return a damaged item.",
    "What are your opening hours?",
];

foreach (var tenant in new[] { "retail", "support" })
{
    foreach (var prompt in prompts)
    {
        try
        {
            var r = await gateway.CompleteAsync(new LlmRequest(tenant, prompt, MaxOutputTokens: 300));
            var note = r.FromCache ? "cache" : r.Fallbacks.Count > 0 ? $"fallback after {r.Fallbacks.Count} issue(s)" : "direct";
            Console.WriteLine($"{tenant,-8} {r.Tier,-5} {r.Provider,-9} {r.Model,-14} ${r.CostUsd:0.000000}  {note}");
        }
        catch (BudgetExceededException ex)
        {
            Console.WriteLine($"{tenant,-8} BLOCKED  {ex.Message}");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Breaker state for 'primary': {gateway.BreakerState("primary")}");
Console.WriteLine();
Console.WriteLine(gateway.UsageReport());

internal sealed class SimulatedProvider(string name, int failFromCall) : ILlmProvider
{
    private int _calls;

    public string Name { get; } = name;

    public Task<LlmResponse> CompleteAsync(ModelSpec model, LlmRequest request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _calls);
        if (n == 2)
        {
            throw new TransientLlmException("429 rate limited") { RetryAfter = TimeSpan.FromMilliseconds(20) };
        }

        if (n >= failFromCall)
        {
            throw new TransientLlmException("503 service unavailable");
        }

        var input = TokenEstimator.Estimate(request.Prompt) + 150; // prompt plus a system prompt
        return Task.FromResult(new LlmResponse($"[{model.Model}] answer", input, Math.Min(request.MaxOutputTokens, 120)));
    }
}
