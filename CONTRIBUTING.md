# Contributing to iRoute

Contributions should preserve the runtime's central constraint: improve validated task completion without adding unjustified model calls, context, latency, or infrastructure.

1. Open an issue for behavior or contract changes.
2. Add or update an ADR for architectural changes.
3. Update OpenAPI and JSON Schemas before SDK code.
4. Add unit, contract, regression, cost, and latency evidence appropriate to the change.
5. Keep provider-specific behavior outside the task-planning core.

Pull requests must pass formatting, build, unit tests, architecture tests, schema validation, secret scanning, dependency review, and evaluation-regression gates.
