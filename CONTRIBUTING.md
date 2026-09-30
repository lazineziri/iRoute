# Contributing to iRoute

Contributions should improve validated task completion without adding
unjustified model calls, context, latency, cost, permissions, or infrastructure.
By participating, you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).
Report vulnerabilities privately through [SECURITY.md](SECURITY.md).

## Before changing code

- Search issues and pull requests.
- Open an issue first for public-contract, persistence, security, architecture,
  routing-policy, or externally visible behavior changes.
- Keep one pull request focused on one coherent outcome.
- Read [architecture](docs/architecture.md) and the relevant accepted ADR.

## Development loop

```bash
dotnet restore iRoute.slnx
dotnet format iRoute.slnx --verify-no-changes --severity warn --no-restore
dotnet build iRoute.slnx --configuration Release --no-restore
dotnet test --solution iRoute.slnx --configuration Release --no-build
dotnet pack iRoute.slnx --configuration Release --no-build --output artifacts/packages
```

Container/deployment changes also run:

```bash
docker compose -f deploy/compose.sqlite.yaml config --quiet
docker compose -f deploy/compose.yaml config --quiet
docker build --file deploy/Dockerfile --target runtime --tag iroute:check .
docker run --rm iroute:check help
```

## Six-project rule

Do not add wrapper layer directories or another production project without an
accepted architecture decision.

- Common: all cross-project contracts, DTOs, interfaces, ports, options, enums,
  and shared primitives; no implementations.
- Services: routing, policy, execution, capability, gateway, and validation behavior.
- Data: EF Core, entities, stores, migrations, leases, and persistence behavior.
- Core: the small stable execution facade; no general business-logic dumping ground.
- Runtime: the only executable/composition root, including API, worker,
  migration, client, CLI, identity, and telemetry hosting.
- Tests: architecture and behavior verification.

Services, Data, and Core reference only Common. Runtime references those four.
Tests may reference all five production projects. Architecture tests enforce the
graph and the single contract assembly.

Keep provider-specific protocols behind the generic gateway, database entities
inside Data, and HTTP/DI concerns inside Runtime. Organize by cohesive feature;
split large responsibilities rather than hiding them in catch-all files or
arbitrary partial classes.

## Contracts and compatibility

The public source of truth is `spec/`; Common is its .NET representation.
Compatible `v1` additions remain optional. Removals, renames, narrowed values,
stronger requirements, or changed meaning are breaking and require a new major
contract. Never edit a compatibility snapshot just to silence a regression.

Public changes update the implementation, OpenAPI/schema material, snapshot,
tests, documentation, and `CHANGELOG.md` together. Stored-state changes include
an additive migration and explicit upgrade/rollback evidence.

## Evidence by change type

| Change | Minimum evidence |
|---|---|
| Common/public contract | focused tests plus OpenAPI/schema/snapshot review |
| Services/Core behavior | success, failure, cancellation, and deadline tests |
| Data/migration | SQLite and PostgreSQL reasoning/tests plus rollback impact |
| Runtime/API/identity | endpoint/startup validation and negative authorization tests |
| CLI/client | protocol and failure behavior tests |
| Container/Kubernetes | image build, command smoke test, and manifest validation |
| Security boundary | threat explanation and adversarial negative tests |
| Routing/model profile | measured or explicitly synthetic evaluation evidence |

## Pull requests

Use the template and list exact commands/results. Call out contract, migration,
security, privacy, telemetry, cost, latency, provider, and rollback impact.
Conventional Commit subjects are preferred, for example `feat(services): ...`,
`fix(data): ...`, or `docs(operations): ...`.

Maintainers merge after applicable `required`, CodeQL, secret-scan, and review
gates pass. They may request an ADR, split an oversized change, or reject a
change that weakens tenant isolation, compatibility, safety, or project
boundaries.

## Contribution terms

Contributions are accepted under Apache-2.0 section 5. Do not submit customer
data, credentials, proprietary evaluation data, private advisories, or code with
an incompatible license. Governance and escalation are described in
[GOVERNANCE.md](GOVERNANCE.md).
