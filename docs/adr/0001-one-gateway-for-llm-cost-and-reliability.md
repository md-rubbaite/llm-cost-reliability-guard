# ADR 0001: One gateway for LLM cost and reliability

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

LLM features moved from pilots to production. Two problems now show up together:

- **Cost** is usage-based and hard to predict. One tenant, a loop in an agent or an oversized prompt can
  consume a month's budget in hours, and finance learns about it from the invoice.
- **Reliability** depends on external providers that rate-limit (HTTP 429), have regional outages and
  slow down under load. Without care, every caller adds its own retry loop and the retries make the
  outage worse.

Teams were solving these in each service, differently, and usually only after an incident.

## Decision

All LLM calls go through `LlmGateway`, in-process, in this order:

1. **Route.** An explicit tier wins; otherwise long or complex prompts go to the Large tier and the rest
   to the cheaper Small tier. Only models whose context window fits the request are considered.
2. **Cache.** Exact match on tenant, tier and a normalised prompt hash, with TTL and LRU bounds. Tenants
   never share entries. Semantic caching is out of scope until there is a validated similarity threshold.
3. **Reserve budget.** The worst-case cost (estimated input plus maximum output, at the most expensive
   candidate's price) is reserved before calling. Concurrent calls therefore cannot overspend.
4. **Call with resilience.** Per-attempt timeout; retries only for transient errors, with exponential
   backoff, full jitter and `Retry-After`; a circuit breaker per provider so an outage fails fast.
5. **Fall back.** If a model is exhausted or its circuit is open, the next model of the same tier is tried
   (typically another provider). Non-retryable errors skip straight to the next model.
6. **Settle and record.** The reservation is replaced with the actual cost from the provider's token
   counts, and a usage record is written for reporting and chargeback. An alert fires once per tenant and
   month when spend crosses a threshold.

Providers sit behind `ILlmProvider`, so Azure OpenAI, Anthropic, Bedrock or a self-hosted model plug in
without changing callers.

## Consequences

- Positive: spend is capped before it happens; outages degrade to a fallback instead of errors; one place
  for metrics and cost reports; providers can be swapped by configuration.
- Negative: worst-case reservations can refuse a call that would have fitted if a cheaper model had
  answered; tiers chosen by heuristics can be wrong, so callers can set the tier explicitly; the cache is
  in-memory per process (a shared cache such as Redis is the next step for multi-instance deployments).
- Prices in the catalog must be kept current from each provider's price list; the sample uses placeholders.
