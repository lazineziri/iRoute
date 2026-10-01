# Version baseline

| Component | Baseline | Source of truth |
|---|---|---|
| Product release line | `0.1.0-alpha.4` | `release.json` and production `.csproj` files |
| Public HTTP API | `v1` | `spec/openapi/iroute.v1.yaml` |
| JSON Schema draft | 2020-12 | `release.json` and `spec/schemas` |
| .NET SDK | minimum `10.0.100`, latest compatible feature band | `global.json` |
| Target framework | `net10.0` | `Directory.Build.props` |
| C# | `14.0` | `Directory.Build.props` |
| PostgreSQL container for local profile | `18.4-alpine` | `deploy/compose.yaml` |

Publication requires a verified annotated `v0.1.0-alpha.4` tag and a successful
release workflow. Historical registries can contain older package
IDs and non-.NET clients; they are not part of the current release line.

Dependency versions are centrally managed in `Directory.Packages.props`.
Dependabot checks NuGet, Docker, and GitHub Actions dependencies weekly.
