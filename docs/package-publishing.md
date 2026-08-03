# Public package publishing

iRoute has one server runtime and six thin client SDKs. The API, execution
worker, and migration job are distributed as container images. Language
registries distribute clients for the public HTTP and SSE contract; installing
an SDK does not install or start the runtime.

## Public coordinates

| Language | Registry | Coordinate |
|---|---|---|
| .NET | NuGet | `iRoute.Sdk` |
| Node.js | npm | `@iroute-dev/sdk` |
| Python | PyPI | `iroute` |
| Java | Maven Central | `dev.iroute:iroute-sdk` |
| PHP | Packagist | `iroute/sdk` |
| Rust | crates.io | `iroute-sdk` |

`0.1.0-alpha.2` is published on npm, PyPI, NuGet, and crates.io. Packagist
awaits submission of the mirror, and Maven Central is not yet published because
the build does not produce the signed source and javadoc artifacts Central
requires.

`@iroute` on npm belongs to an unrelated account, which is why the Node.js
client is published under `@iroute-dev`.

## Security model

- Publish only from an immutable `v<version>` tag matching `release.json`.
- Run the complete release gate and rebuild artifacts from that tag.
- Prefer registry trusted publishing with GitHub Actions OIDC. Do not create a
  long-lived automation token when a registry supports short-lived identity.
- Put each registry job behind a GitHub environment with required maintainer
  approval.
- Never put a token in a remote URL, workflow file, command argument, package,
  release archive, log, issue, or documentation page.
- Inspect package contents and run Gitleaks against the complete Git history
  before the first public push and before every release.
- Published versions are immutable. Correct a bad release with a new version;
  never replace an existing package payload.

The manual `publish-sdks.yml` workflow supports npm, NuGet, PyPI, and crates.io.
It refuses branch builds and requires its selected Git ref to be the exact
release tag.

## Registry bootstrap

### npm

The `@iroute-dev` scope must belong to the maintainer account or organization
and must be created before the first publish. The unscoped `@iroute` scope is
owned by an unrelated npm account, which is why the client publishes under
`@iroute-dev/sdk`. npm requires the first package to exist before a trusted
publisher can be attached.
Bootstrap the verified tarball through npm's staged-publishing flow, approve it
with account 2FA, then configure this trusted publisher:

- GitHub owner: `lazineziri`
- repository: `iRoute`
- workflow: `publish-sdks.yml`
- environment: `npm`
- allowed action: `npm publish`

After proving OIDC publishing, disallow traditional publish tokens.

### NuGet

Create an owner-level trusted publishing policy for `lazineziri/iRoute`,
workflow `publish-sdks.yml`, environment `nuget`. Add the NuGet profile name as
the non-secret GitHub Actions variable `NUGET_USER`. The workflow publishes
`iRoute.Contracts` first because `iRoute.Sdk` depends on that package.

### PyPI

Create a pending trusted publisher for project `iroute`, owner `lazineziri`,
repository `iRoute`, workflow `publish-sdks.yml`, environment `pypi`. A pending
publisher can create the first release without a long-lived PyPI API token.

### crates.io

crates.io requires the first crate release to be published manually. Package
and inspect it with `cargo publish --dry-run`, publish the first verified
version from a clean tag, and immediately remove the local token with
`cargo logout`. Then attach the GitHub trusted publisher to the crate and use
the `crates` environment for later releases.

### Maven Central

Verify the `dev.iroute` namespace through ownership of `iroute.dev`. Maven
Central additionally requires signed main, source, and Javadoc artifacts plus
complete POM metadata. Central credentials and signing material must be
configured outside the repository. Maven publication remains disabled until
namespace verification and signing recovery have been tested.

### Packagist

Packagist requires `composer.json` at the root of its VCS repository. Publish
the `sdks/php` subtree to a dedicated public `lazineziri/iroute-php` mirror,
submit that repository to Packagist, and let VCS tags provide package versions.
The mirror must contain only the PHP SDK, README, LICENSE, and NOTICE.

## First-release order

1. Push the reviewed source repository and immutable release tag.
2. Create the GitHub release and independently verify its checksums.
3. Publish PyPI and NuGet through pending/owner OIDC policies.
4. Bootstrap npm through staged publishing, then enable OIDC.
5. Bootstrap crates.io manually, then enable OIDC.
6. Publish the PHP mirror and submit it to Packagist.
7. Publish Maven Central only after namespace and signing validation.
8. Install every package into an empty representative application and execute
   one request against a clean local iRoute runtime.
