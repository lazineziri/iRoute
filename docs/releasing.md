# Releasing

One workflow owns package verification and publication:
`.github/workflows/release.yml`.

Manual dispatch is a dry run: it validates metadata, formatting, build, tests,
five packages, source archive, release notes, checksums, and the runtime image
with SQLite and PostgreSQL migrations and API/worker execution. The release
test job also runs PostgreSQL contention/fencing/fairness/upgrade tests against
a disposable service database. A verified
annotated tag additionally publishes NuGet and GHCR, then
creates the GitHub prerelease only after both registries succeed.

## Before tagging

1. Start from clean `main` and pull the latest origin.
2. Choose a SemVer version. Update `release.json` and all five production
   `.csproj` files to the same value, and set `releaseDate` to the intended UTC
   release date.
3. Add `docs/releases/<version>.md` and point `release.json.releaseNotes` to it.
4. Update `CHANGELOG.md`, `README.md`, deployment image references, and version
   baseline.
5. Run locally:

   ```bash
   dotnet restore iRoute.slnx
   dotnet format iRoute.slnx --verify-no-changes --severity warn --no-restore
   dotnet build iRoute.slnx --configuration Release --no-restore
   dotnet test --solution iRoute.slnx --configuration Release --no-build
   dotnet pack iRoute.slnx --configuration Release --no-build --output artifacts/release
   docker compose -f deploy/compose.sqlite.yaml config --quiet
   docker compose -f deploy/compose.yaml config --quiet
   docker build --file deploy/Dockerfile --target runtime --tag iroute:release-check .
   docker run --rm iroute:release-check help
   ```

6. Push the release-preparation commit and wait for `ci`, `codeql`, and
   `secret-scan` to succeed.
7. Manually dispatch `release` on that commit and confirm `verify-release` and
   `verify-container` pass.

## Tag and publish

Create a signed annotated tag on the verified commit:

```bash
git tag --sign v0.1.0-alpha.4 --message 'iRoute 0.1.0-alpha.4'
git push origin v0.1.0-alpha.4
```

The workflow rejects a lightweight tag, a GitHub-unverified signature, a tag
that does not match `release.json`, missing release notes, formatting drift,
test failure, package count/name mismatch, or a failed container build.

Approve the `nuget` environment deployment after reviewing the tag and package
artifact. The workflow then:

1. publishes five NuGet packages with an OIDC-issued short-lived key;
2. publishes the multi-architecture `ghcr.io/lazineziri/iroute` image with
   version/channel tags, provenance, and SBOM;
3. creates the GitHub prerelease with NuGet files, source archive, and
   `SHA256SUMS`.

The GitHub release is deliberately last, so it never advertises a release whose
registry publication failed.

## Verification after publication

- Install the exact tool version from NuGet and run `iroute help`.
- Pull the exact GHCR version by digest and run `help`, `migrate help`, and a
  SQLite API health/dashboard smoke test.
- Verify all five package versions and dependency metadata on NuGet.
- Download release assets and run `sha256sum --check SHA256SUMS` from the asset
  directory.
- Confirm the GitHub Actions run, image provenance, and SBOM are visible.

Do not move or recreate the signed version tag. If publication is wrong, keep
the release immutable, document the issue, fix forward with a new version, and
deprecate the affected package version in its registry where appropriate.
