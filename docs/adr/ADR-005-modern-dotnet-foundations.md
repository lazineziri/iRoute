# ADR-005: Modern .NET foundations

Status: Accepted

## Context

The repository is now intentionally .NET-only. It previously carried small
project-owned abstractions and runtime reflection in places where the current
.NET platform has native, analyzable alternatives. Configuration failures could
also surface after a host had started, and style conventions were documented but
not enforced by the build.

At the same time, iRoute owns policy-sensitive retry, routing, cost, and deadline
semantics. Adopting framework features indiscriminately could duplicate attempts
or hide those decisions.

## Decision

- Use the BCL `TimeProvider` as the single clock and timer abstraction.
- Use the options pattern with startup validation. Prefer the compile-time
  options validation generator for attribute-expressible rules and focused
  `IValidateOptions<T>` implementations for cross-property rules.
- Use System.Text.Json source generation at known public serialization
  boundaries.
- Freeze immutable lookup tables after construction.
- Enforce nullable analysis, recommended .NET analyzers, warnings-as-errors,
  code-style analysis, and repository formatting centrally.
- Keep iRoute's explicit gateway resilience policy. Do not layer a generic HTTP
  retry pipeline over it.
- Keep public APIs and assembly namespaces stable while organizing source by
  layer, feature, and cohesive responsibility.

## Consequences

Time and delays are deterministic under an injected provider, configuration
errors fail during host startup, known wire types can avoid reflection metadata,
and immutable registries have lower lookup overhead. The compiler and formatter
now enforce the baseline instead of relying on reviewer memory.

The solution still contains historically large orchestration and persistence
types. They must be decomposed by capability and store responsibility in staged,
behavior-preserving changes; this ADR deliberately does not conceal that debt
behind arbitrary partial classes. The absence of in-repository tests after the
.NET-only reset makes large behavioral refactors inappropriate until a new .NET
verification strategy is approved.
