namespace LlmGuard;

/// <summary>
/// Per-tenant monthly spend limits with reservations. Before a call the worst-case cost is reserved;
/// afterwards the reservation is replaced with the actual cost. Concurrent calls therefore cannot
/// overspend a budget, which a simple "check then call" would allow.
/// </summary>
public sealed class BudgetLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _limits;
    private readonly Dictionary<(string Tenant, string Period), decimal> _spent = [];
    private readonly Dictionary<(string Tenant, string Period), decimal> _reserved = [];
    private readonly IClock _clock;

    public BudgetLedger(IReadOnlyDictionary<string, decimal> monthlyLimitsUsd, decimal alertThreshold = 0.8m, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(monthlyLimitsUsd);
        if (alertThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alertThreshold));
        }

        _limits = monthlyLimitsUsd.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        AlertThreshold = alertThreshold;
        _clock = clock ?? SystemClock.Instance;
    }

    public decimal AlertThreshold { get; }

    /// <summary>Raised once per tenant and month when spend crosses the alert threshold.</summary>
    public event Action<string, decimal, decimal>? ThresholdCrossed;

    private readonly HashSet<(string, string)> _alerted = [];

    private string Period => _clock.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public BudgetReservation Reserve(string tenant, decimal worstCaseUsd)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        lock (_gate)
        {
            if (!_limits.TryGetValue(tenant, out var limit))
            {
                throw new BudgetExceededException($"Tenant '{tenant}' has no budget configured.");
            }

            var key = (tenant, Period);
            var committed = _spent.GetValueOrDefault(key) + _reserved.GetValueOrDefault(key);
            if (committed + worstCaseUsd > limit)
            {
                throw new BudgetExceededException(
                    $"Tenant '{tenant}' budget of ${limit:0.00##} for {key.Period} would be exceeded (committed ${committed:0.0000}, request up to ${worstCaseUsd:0.0000}).");
            }

            _reserved[key] = _reserved.GetValueOrDefault(key) + worstCaseUsd;
            return new BudgetReservation(this, key, worstCaseUsd);
        }
    }

    internal void Settle((string Tenant, string Period) key, decimal reserved, decimal actual)
    {
        decimal? crossed = null;
        decimal limit;
        lock (_gate)
        {
            _reserved[key] = _reserved.GetValueOrDefault(key) - reserved;
            _spent[key] = _spent.GetValueOrDefault(key) + actual;
            limit = _limits[key.Tenant];
            if (_spent[key] >= limit * AlertThreshold && _alerted.Add(key))
            {
                crossed = _spent[key];
            }
        }

        if (crossed is { } spent)
        {
            ThresholdCrossed?.Invoke(key.Tenant, spent, limit);
        }
    }

    public decimal Spent(string tenant)
    {
        lock (_gate)
        {
            return _spent.GetValueOrDefault((tenant, Period));
        }
    }

    public decimal Remaining(string tenant)
    {
        lock (_gate)
        {
            var key = (tenant, Period);
            return _limits.GetValueOrDefault(tenant) - _spent.GetValueOrDefault(key) - _reserved.GetValueOrDefault(key);
        }
    }
}

/// <summary>A held amount. Settle with the actual cost, or dispose to release it (for example after a failure).</summary>
public sealed class BudgetReservation : IDisposable
{
    private readonly BudgetLedger _ledger;
    private readonly (string Tenant, string Period) _key;
    private readonly decimal _amount;
    private bool _done;

    internal BudgetReservation(BudgetLedger ledger, (string Tenant, string Period) key, decimal amount)
    {
        _ledger = ledger;
        _key = key;
        _amount = amount;
    }

    public void Settle(decimal actualUsd)
    {
        if (_done)
        {
            throw new InvalidOperationException("Reservation already settled.");
        }

        _done = true;
        _ledger.Settle(_key, _amount, actualUsd);
    }

    public void Dispose()
    {
        if (!_done)
        {
            _done = true;
            _ledger.Settle(_key, _amount, 0m);
        }
    }
}
