# Documentation

The Markdown files in this directory describe the current .NET-only source tree.
Historical release notes are preserved under `releases/`; they describe the
repository as it existed at each tag and are not current architecture guidance.

## Start here

- [Installation](installation.md) — source, tool, and container setup
- [Model providers](model-providers.md) — OpenAI/Anthropic keys, local subscriptions, and billing limits
- [Architecture](architecture.md) — six projects, allowed dependencies, and request flow
- [Client and CLI usage](client-usage.md) — submitting, polling, streaming, approvals, and errors
- [Operations](operations.md) — configuration, identity, storage, workers, gateways, telemetry, and rollback
- [Tenant quotas](tenant-quotas.md) — admission, accounting, fairness, HTTP limits, and operational caveats
- [Project status](project-status.md) — implemented capabilities and known gaps
- [Product benchmark](product-benchmark.md) — opt-in live comparisons, token accounting, results, and limits

## Contracts and compatibility

- [Product and engineering specification](iRoute-Product-Engineering-Specification.md)
- [Compatibility promise](compatibility.md)
- [Contract versioning](contract-versioning.md)
- [Version baseline](version-baseline.md)
- [OpenAPI](../spec/openapi/iroute.v1.yaml)
- [JSON Schemas](../spec/schemas)
- [SSE event contract](../spec/events/sse-v1.md)
- [Error taxonomy](../spec/errors/error-taxonomy.v1.md)

## Delivery

- [Deployment profiles](../deploy/README.md)
- [Package publishing](package-publishing.md)
- [Release procedure](releasing.md)
- [Release notes](releases)

## Decisions

Accepted architectural decisions live under [`adr/`](adr/). If an ADR and a
current guide appear to conflict, open an issue: implementation, current guide,
and accepted decision should converge rather than creating a second convention.
