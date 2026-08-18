# Release process

Releases are immutable, version-aligned, and built from reviewed commits. The
canonical release metadata is [release.json](../release.json); package manifests,
container labels, Kubernetes tags, changelog entries, and release notes must
agree with it.

## Release artifacts

The prerelease builder produces:

- source archive;
- `iRoute.Contracts` NuGet package;
- `iRoute.Sdk` NuGet package;
- `iRoute.Cli` .NET tool package;
- release notes;
- `SHA256SUMS` covering every distributed file.

The repository supports the .NET SDK only. NuGet publication is a separate,
explicit maintainer action after the GitHub release artifacts are verified.

Container images are built from the same commit using the `api`, `worker`, and
`migrate` Dockerfile targets. Operators must publish immutable tags to their
controlled registry and replace the registry placeholders in the Kubernetes
reference before production use.

## Prepare

1. Choose a SemVer version and channel. Update `release.json` first.
2. Align every package version and immutable image tag.
3. Move user-visible `Unreleased` entries into the versioned changelog section.
4. Add `docs/releases/<version>.md` with capabilities, known limits, upgrade,
   rollback, and checksum guidance.
5. Run dependency, secret, license, contract, migration, and compatibility
   review. Resolve every unexplained change.
6. Confirm the security advisory channel is enabled and a maintainer can receive
   private reports.
7. Confirm the release notes explicitly identify the experimental alpha status,
   expected breaking changes, absence of production/security-response SLAs,
   reference-connector limitations, unvalidated provider measurements, and the
   self-hoster security/operations boundary.
8. Prove `DevelopmentHeaders` fails startup outside Development and that every
   production-shaped manifest defaults to JWT.

## Verify from a clean checkout

Run the full installation path in [installation.md](installation.md), then pack
the supported .NET artifacts into a new empty directory:

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --configuration Release --no-restore
dotnet pack src/iRoute.Contracts --configuration Release --no-restore --output /tmp/iroute-release
dotnet pack src/iRoute.Sdk.DotNet --configuration Release --no-restore --output /tmp/iroute-release
dotnet pack src/iRoute.Cli --configuration Release --no-restore --output /tmp/iroute-release
```

Inspect the packages and verify every line in the workflow-produced
`SHA256SUMS`. Build the
three Docker targets, run the SQLite container smoke test, render the Kubernetes
base with `kubectl kustomize`, and test the PostgreSQL migration/rollback path
against a disposable database.

No release may proceed with a dirty worktree, a failing required check, a
tracked credential/reference document, an unexplained compatibility snapshot
change, a floating image tag, or an unreviewed migration.

## Tag and publish

1. Merge the release commit to the default branch.
2. Create a signed annotated tag `v<version>` on that exact commit.
3. Push the commit and tag. The tag-triggered `release.yml` workflow reruns the
   release gate and artifact build.
4. The workflow creates a GitHub prerelease from the checked release notes and
   attaches the checksummed artifacts. It never publishes on a manual dry run.
5. Download the published assets, recompute SHA-256 digests independently, and
   compare them with `SHA256SUMS`.
6. Publish immutable container and NuGet packages only after the
   GitHub artifacts are verified. Record their digests in the release notes or
   an attestation before announcing availability.

Never move or recreate a published tag. If a release is incorrect, preserve it,
mark it superseded when necessary, and publish a new patch/prerelease version.

## Post-release

- Verify the documented clean install using only published source/assets.
- Verify API liveness/readiness, one deterministic execution, SSE replay, and
  observability.
- Confirm the changelog and support table identify the released version.
- Open the next `Unreleased` section and record follow-up issues.
- Monitor private security advisories and installation reports.

## Rollback and failed publication

Before a GitHub release is public, stop the workflow, delete incomplete draft
artifacts, fix the release commit, and use a new tag. After publication, do not
replace assets under the same version. Publish a correcting version and follow
the application-first rollback procedure in [operations.md](operations.md).

A registry artifact whose checksum does not match is treated as a supply-chain
incident: stop distribution, preserve evidence, rotate affected credentials,
and use the private security process.
