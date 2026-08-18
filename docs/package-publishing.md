# Public package publishing

iRoute has one server runtime and one supported client SDK. The API, execution
worker, and migration job are distributed as container images. The .NET client
and public contracts are distributed through NuGet.

## Public coordinates

| Package | Registry | Status |
|---|---|---|
| `iRoute.Sdk` | NuGet | Published |
| `iRoute.Contracts` | NuGet | Published |
| `iRoute.Cli` | GitHub release artifact / local .NET tool package | Supported |

`0.1.0-alpha.3` is the current package version. Previously published non-.NET
prereleases remain immutable historical artifacts and do not represent the
current supported client surface.

## Security model

- Publish only from an immutable `v<version>` tag matching `release.json`.
- Run the complete release gate and rebuild artifacts from that tag.
- Use NuGet trusted publishing with GitHub Actions OIDC; do not add a long-lived
  package token.
- Keep the publish job behind the `nuget` GitHub environment.
- Never put a credential in a remote URL, workflow file, command argument,
  package, release archive, log, issue, or documentation page.
- Inspect package contents and run Gitleaks against the complete history before
  every public push.
- Published versions are immutable. Correct a bad release with a new version.

The manual `publish-nuget.yml` workflow refuses branch builds and checks out the
exact signed release tag before building. It publishes `iRoute.Contracts`
before `iRoute.Sdk` because the SDK depends on the contracts package.

## NuGet trusted publishing

Maintain an owner-level trusted publishing policy for:

- GitHub owner: `lazineziri`
- repository: `iRoute`
- workflow: `publish-nuget.yml`
- environment: `nuget`

Store the NuGet profile name as the non-secret GitHub Actions variable
`NUGET_USER`. The workflow obtains a short-lived API key through `NuGet/login`.

## Release order

1. Merge the reviewed release commit.
2. Push its signed annotated `v<version>` tag.
3. Let `release.yml` create the checksummed GitHub prerelease.
4. Verify the downloaded manifest and SHA-256 checksums independently.
5. Dispatch `publish-nuget.yml` with the exact version.
6. Install `iRoute.Sdk` and `iRoute.Contracts` into an empty .NET application
   and execute one request against a clean local runtime.

Container publication remains operator-controlled until a canonical public
container registry is configured.
