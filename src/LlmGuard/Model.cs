namespace LlmGuard;

/// <summary>Capability tier. Small models are cheap and fast; Large models handle harder tasks.</summary>
public enum ModelTier
{
    Small,
    Large,
}

/// <summary>A model offered by a provider, with list prices in USD per million tokens.</summary>
public sealed record ModelSpec(string Provider, string Model, ModelTier Tier, decimal InputPerMillion, decimal OutputPerMillion, int ContextTokens)
{
    public decimal Cost(int inputTokens, int outputTokens) =>
        ((inputTokens * InputPerMillion) + (outputTokens * OutputPerMillion)) / 1_000_000m;
}

public sealed record LlmRequest(string Tenant, string Prompt, int MaxOutputTokens = 512, ModelTier? Tier = null, bool AllowCache = true);

public sealed record LlmResponse(string Text, int InputTokens, int OutputTokens);

/// <summary>What the caller gets back, including what it cost and how it was served.</summary>
public sealed record GuardedResponse(
    string Text,
    string Provider,
    string Model,
    ModelTier Tier,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    bool FromCache,
    int Attempts,
    IReadOnlyList<string> Fallbacks);

/// <summary>A provider SDK or HTTP client behind one interface (Azure OpenAI, Anthropic, Bedrock, a local model…).</summary>
public interface ILlmProvider
{
    string Name { get; }

    Task<LlmResponse> CompleteAsync(ModelSpec model, LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>Thrown by providers for failures worth retrying: rate limits, 5xx, timeouts.</summary>
public sealed class TransientLlmException : Exception
{
    public TransientLlmException()
    {
    }

    public TransientLlmException(string message)
        : base(message)
    {
    }

    public TransientLlmException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public TimeSpan? RetryAfter { get; init; }
}

public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException()
    {
    }

    public BudgetExceededException(string message)
        : base(message)
    {
    }

    public BudgetExceededException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class AllProvidersFailedException : Exception
{
    public AllProvidersFailedException()
    {
    }

    public AllProvidersFailedException(string message)
        : base(message)
    {
    }

    public AllProvidersFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Testable time source.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Rough token estimate (about four characters per token for English text). Used only for pre-call budget reservations.</summary>
public static class TokenEstimator
{
    public static int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Math.Max(1, (text.Length + 3) / 4);
    }
}
