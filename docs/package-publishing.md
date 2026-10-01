# Package publishing

The .NET-only release line publishes one coordinated package set. Every artifact
uses the exact version from `release.json` and the production project files.

| Artifact | Registry | Purpose |
|---|---|---|
| `iRoute.Common` | NuGet | contracts, DTOs, ports, options, and primitives |
| `iRoute.Services` | NuGet | execution, routing, policy, gateway, and capability implementations |
| `iRoute.Data` | NuGet | EF Core stores, entities, and migrations |
| `iRoute.Core` | NuGet | stable execution facade |
| `iRoute` | NuGet tool | Runtime server, worker, migration runner, client, and CLI |
| `ghcr.io/lazineziri/iroute` | GitHub Container Registry | unified multi-mode Linux runtime image |
| source archive/checksums | GitHub Release | reproducible source handoff and integrity manifest |

`0.1.0-alpha.4` is the coordinated .NET release version. Registry publication is
confirmed only when its verified-tag workflow completes. Older registry entries—including previous
package IDs and non-.NET clients—are immutable historical releases, not current
support targets.

## NuGet trusted publishing

The `nuget` GitHub environment protects publication. Configure a NuGet trusted
publishing policy for this repository, owner, workflow `release.yml`, and
environment `nuget`. Store the NuGet user/profile name as the environment
variable `NUGET_USER`; do not store a long-lived API key.

The workflow obtains a short-lived key through `NuGet/login`, then publishes in
dependency order with `--skip-duplicate` for safe retries:

```text
Common -> Services -> Data -> Core -> iRoute tool
```

NuGet resolves the actual dependency graph; the order above guarantees Common
exists before packages that reference it and publishes the complete tool last.

## Container publication

The tag workflow builds Linux AMD64 and ARM64 variants from `deploy/Dockerfile`,
publishes immutable `<version>` and moving `<channel>` tags, and attaches build
provenance plus an SBOM. Production deployments should pin the immutable version
or manifest digest, not the channel tag.

The image defaults to `serve` and accepts `worker`, `migrate ...`, or client
commands without changing images.

## Local package verification

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --configuration Release --no-restore
dotnet test --solution iRoute.slnx --configuration Release --no-build
dotnet pack iRoute.slnx \
  --configuration Release \
  --no-build \
  --output artifacts/packages
```

Exactly five `.nupkg` files must be produced and their filenames must contain the
declared version. Never publish packages built from a dirty tree or a branch
workflow artifact.
