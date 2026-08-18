# Contributing to iRoute

Thank you for helping improve iRoute. Contributions should preserve the central
constraint: improve validated task completion without adding unjustified model
calls, context, latency, cost, permissions, or infrastructure.

By participating, you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). For
security vulnerabilities, follow [SECURITY.md](SECURITY.md) instead of opening a
public issue.

## Before opening a change

- Search existing issues and pull requests.
- Use a discussion or feature request for a design that changes public behavior.
- Open a security advisory for a suspected vulnerability.
- Keep one pull request focused on one coherent outcome.

Small bug fixes and documentation corrections can go directly to a pull
request. Open an issue first for public-contract, persistence, security,
architecture, routing-policy, or externally visible behavior changes.

## Development setup

The canonical clean setup is documented in [docs/installation.md](docs/installation.md).
For the standard development loop:

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --no-restore
```

CI restores and builds the complete supported .NET runtime and client surface.

## Architecture rules

Read [docs/architecture.md](docs/architecture.md) before changing runtime code.
`Runtime` and `Infrastructure` are sibling layers over `Contracts` and `Core`.
Only hosts may compose both layers. The .NET SDK depends only on public
contracts, and the CLI depends on the SDK.

- Update OpenAPI and JSON Schemas before changing .NET SDK wire behavior.
- Keep provider-specific protocols behind the generic model gateway.
- Keep transport and persistence concerns out of Core.
- Follow the `layer/project/feature` source layout and do not add unrelated
  logic to a project root or catch-all services file.
- Treat connector responses as untrusted until projected and validated.
- Preserve tenant scoping at every persistence and query boundary.
- Add an ADR for a durable architectural or product-boundary decision.

## Compatibility and versioning

The compatibility promise is defined in [docs/compatibility.md](docs/compatibility.md)
and enforced against the v1 snapshot. In short:

- Compatible additions remain optional.
- Removal, renaming, type changes, stronger validation, or changed meaning are
  breaking.
- Breaking HTTP changes require a new API major and migration guide.
- Stored task, capability, policy, and artifact semantics are versioned even
  when their JSON shape does not change.
- Never edit an established compatibility snapshot to make a breaking change
  appear compatible.

Every user-visible change adds an entry under `Unreleased` in
[CHANGELOG.md](CHANGELOG.md). Maintainers assign the release version.

## Verification expected by change type

| Change | Minimum evidence |
|---|---|
| Core/runtime behavior | Successful strict .NET build and focused review evidence |
| Public HTTP or event contract | Updated OpenAPI and JSON Schema definitions |
| Routing or model profile | Documented evaluation evidence |
| Persistence or migration | Reviewed SQLite/PostgreSQL migration evidence |
| .NET SDK or CLI | Successful .NET build and protocol review |
| Container or Kubernetes | Successful image builds and manifest review |
| Security boundary | Threat explanation and adversarial review in the pull request |

Verification evidence must cover the failure path as well as the intended path.
Do not change a published contract snapshot merely to silence a regression
without explaining the compatibility impact.

## Pull requests

Use the pull request template. A reviewable pull request:

1. Explains the problem and the chosen boundary.
2. Links its issue or ADR when required.
3. Includes proportional verification evidence and documentation in the same change.
4. Calls out public-contract, migration, security, privacy, cost, and rollback
   impact explicitly.
5. Passes the strict build, packaging, container, dependency-review,
   secret-scanning, and other gates that apply.

Use Conventional Commit subjects such as `feat(runtime): ...`, `fix(sdk): ...`,
or `docs(operations): ...`. Maintainers may squash a pull request while
preserving a thoughtful subject and body.

## Contribution terms

Contributions are accepted under Apache-2.0 as described by section 5 of the
[license](LICENSE). By submitting a contribution, you represent that you have
the right to do so and that it is not confidential or encumbered by incompatible
terms. Do not submit customer data, credentials, proprietary evaluation data,
or code copied from a source whose license is incompatible with Apache-2.0.

## Review and decisions

Maintainers merge changes after required checks and review are complete. A
maintainer may request an ADR, split an oversized change, or reject a change
that weakens safety, compatibility, isolation, or the project boundary. Project
roles and escalation are described in [GOVERNANCE.md](GOVERNANCE.md).
