# Compatibility promise

This document defines what adopters can rely on. It complements the mechanical
v1 rules in [contract-versioning.md](contract-versioning.md).

## Release maturity

`0.1.0-alpha.1` is an alpha prerelease. Runtime internals, configuration that is
not listed in public documentation, deterministic reference-adapter behavior,
and source-level extension points may change between prereleases. Such changes
are still documented in the changelog and migration notes.

Alpha status does not weaken the published v1 wire contract. OpenAPI operations,
JSON Schema identifiers, SSE ordering, existing fields, status values, error
codes, and enum values follow the compatibility rules below.

## Public v1 contract

Within `/v1` and the v1 schemas:

- Existing endpoints, fields, events, statuses, error codes, and enum members
  are not removed or renamed.
- Existing types, meanings, defaults, formats, and validation ranges are not
  changed incompatibly.
- Optional fields may be added. They stay optional until all official SDKs pass
  the shared conformance suite.
- Consumers must ignore unknown object fields and event types and safely surface
  unknown enum/status/error values.
- A security correction may narrow accepted input only when the advisory and
  migration guidance explain the incompatibility.

A breaking HTTP change requires `/v2`, new immutable schema identifiers, a
migration guide, and a separately reviewed compatibility baseline. The v1
snapshot is never rewritten to disguise a break.

## Deprecation and support window

Before the first stable runtime release, the latest tagged prerelease is the
only supported application version. The v1 wire contract remains additive
through the alpha line unless a coordinated security fix requires otherwise.

Starting with `1.0.0`, a stable public API major will receive at least 12 months
of deprecation notice before removal and will not be removed without a new API
major. A specific longer window may be announced in release notes; a shorter
window cannot be applied retroactively.

Security support is defined separately in [SECURITY.md](../SECURITY.md).

## SDK compatibility

Official SDK versions track the runtime release train and protocol major.

- SDKs contain protocol transport and typed mappings, not routing behavior.
- A v1 SDK must tolerate additive v1 response fields and events.
- Each SDK release passes the same conformance fixtures.
- A client may lag a newly added optional operation, but existing operations
  must continue to work with a newer additive v1 server.
- SDK language/runtime baselines can change in a prerelease only with a
  changelog entry. Stable baseline changes require a documented deprecation.

## Stored state and migrations

Database migrations are forward by default and follow expand-and-contract
sequencing. A release must permit its API and worker binaries to overlap with
the immediately previous release during rollout. Destructive contract-phase
schema cleanup waits until the rollback window has expired.

Application rollback over an additive schema is supported; automatic data-safe
schema downgrade is not promised. The explicit migration `down` command is an
operator-controlled recovery tool and may lose data. Follow
[operations.md](operations.md) and restore from a tested backup when required.

Task definitions, capabilities, routing policies, artifacts, and stored
semantic decisions carry explicit versions. A behavior change that would
reinterpret stored state requires a new semantic version even if its wire shape
is unchanged.

## Configuration and deployment

Documented environment variables, health endpoints, image targets, and CLI
commands are public operational surfaces. Additive settings may be introduced
with safe defaults. Removing or changing a documented setting requires release
notes and, after `1.0.0`, a deprecation window unless it fixes a vulnerability.

Kubernetes manifests are reference manifests, not a managed service SLA. Image
registries, ingress, TLS, identity, secrets, PostgreSQL, backups, the model
gateway, and production connectors remain operator-owned integrations.

## Compatibility gate

Run these before proposing a public-contract or release change:

```bash
npm run test:contracts
npm run test:sdks
npm run test:release
```

The contract snapshot enforces the existing v1 surface; shared SDK fixtures
enforce client behavior; the release gate enforces version and package metadata.
