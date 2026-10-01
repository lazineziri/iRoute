# Model providers

iRoute has direct .NET HTTP adapters for OpenAI Responses and Anthropic Messages.
No separate SDK project or proxy is required. Native routes currently support
`text.generation` (email-draft JSON) and `text.summarization` (summary JSON), using
buffered structured output. The generic HTTP/NDJSON contract remains supported.

These are alpha adapters. Protocols and failure behavior are tested with fake
responses; live credentials, model entitlement, real quality, and actual billing
must be verified by the operator. Normal tests do not make live inference calls;
the separately opted-in benchmarks consume the operator's provider allowance.

## API keys

Select a model available to your provider account. Do not commit credentials.
For a source checkout, OpenAI requires:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ModelGateway__Mode=OpenAI \
ModelGateway__Model='<provider-model-id>' \
OPENAI_API_KEY='<your-api-key>' \
dotnet run --project src/iRoute.Runtime -- serve --urls http://localhost:8080
```

Anthropic uses `ModelGateway__Mode=Anthropic`, its model identifier, and
`ANTHROPIC_API_KEY`. For an installed tool, replace `dotnet run ... --` with
`iroute`. `ModelGateway__ApiKey` is also supported and takes precedence over the
provider's environment variable. Use a secret manager in hosted environments.

The default endpoints are `https://api.openai.com/v1/responses` and
`https://api.anthropic.com/v1/messages`. A custom `ModelGateway__BaseUrl` is a
base directory, including its prefix (for example `https://proxy.example/v1/`).
Native credentials require HTTPS except on loopback test endpoints. Redirects
are not followed. A native route must use `Buffered` transport.

## Multiple deployments

Use `ModelGateway__Mode=Http` with entries under `ModelGateway:Deployments` to
register both providers in the same runtime. Each deployment's `Adapter` selects
`Http`, `OpenAI`, `Anthropic`, `ClaudeCode`, or `OpenAIChatGPT`; `Provider` is audit
metadata, not an adapter selector. Specify `RouteId`, `GatewayId`, `DeploymentId`,
`Model`, `ModelVersion`, `Capabilities`, `ProfileIds`, and region/residency
metadata. API routes use their own `ApiKey`. Keep every API and worker process
on identical configuration. Priority, circuits, attempt limits, and deadline
allocation still belong to the existing resilience service.

OpenAI adapters optionally accept `ModelGateway__ReasoningEffort` (or a
deployment's `ReasoningEffort`). Omit it to use the model default; select only an
effort supported by the chosen model/account. `low` was used in the small local
diagnostics. See OpenAI's [reasoning-effort guide](https://developers.openai.com/api/docs/guides/reasoning).
This setting is not a hard token or usage ceiling.

## Cost and quality are not measurements

Set `InputCostPerMillionTokens` and `OutputCostPerMillionTokens` on the gateway
or deployment using the current prices for the selected model. API cost is an
estimate from reported token usage and those configured prices, not a reconciled
invoice. Anthropic cached input is counted at the configured
input rate; discounts and cache-write premiums require a more detailed billing
integration before using this as a hard monetary guarantee.

Without prices, `usage.costKnown=false` and the numeric cost placeholder is zero.
That does **not** mean free usage. A dollar ceiling is rejected before sending
an unpriced API request. With prices, a conservative input/output estimate is
checked before sending and reported usage is checked afterward. Provider-side
spending controls are still necessary; this is not prepaid quota reservation.
Optional [tenant admission quotas](tenant-quotas.md) reserve and reconcile
normalized usage in storage, but do not replace provider/account spending controls.

Unknown costs are not recorded as zero-dollar cost histogram samples. Historical
dashboard totals are sums of available estimates, not invoices or subscription
consumption reports.

`ModelGateway__ExpectedQuality` (0.9 by default for native single routes) or a
deployment's `ExpectedQuality` is an operator estimate used by routing/validation, not an
observed accuracy score. Built-in profiles remain synthetic. Before production,
evaluate each real provider/model and register measured profiles with provenance.

## Claude subscription: local official CLI

Anthropic's documented Claude Code integration permits users to sign in to the
unmodified binary with their own subscription. This is distinct from offering
iRoute's own Claude.ai login or collecting/reusing its OAuth tokens, which is
not permitted. Product integrations must also comply with the commercial terms
and preserve the binary's authentication methods. See
[Anthropic's integration policy](https://code.claude.com/docs/en/legal-and-compliance).
Its [Agent SDK billing update](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
states that the proposed billing change was paused and `claude -p` usage still
draws from subscription limits. This is not unrestricted direct Messages API
access or permission to pool subscriptions as a shared inference service.

Install the current official Claude Code CLI and sign in through its own
`claude auth login` command. Then run iRoute locally:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ModelGateway__Mode=ClaudeCode \
ModelGateway__Model='<model-available-to-your-plan>' \
ModelGateway__SubscriptionTenantId=local \
dotnet run --project src/iRoute.Runtime -- serve --urls http://localhost:8080
```

Send requests as tenant `local`. `ModelGateway__ExecutablePath` can select an
installed official binary. iRoute checks safety-flag support before invoking it,
uses a fresh temporary working directory and argument lists rather than a
shell, disables tools, MCP, customizations and transcript persistence, bounds
output/time, and kills the process tree on cancellation. It never reads or
copies the CLI's credential store. API-key and provider-override environment
variables are removed from the child process.

Before inference, `claude auth status --json` must report a recognized first-party
Claude subscription login with no API-key source. Console logins, missing login,
and unknown status formats fail closed. Normal CI excludes authenticated
inference; the [local hardening benchmark](benchmarks/2026-10-01-hardening.md)
includes eight passing final Claude subscription cases. Managed machine policy can still
apply in safe mode, so use a trusted local installation and review that policy.

This adapter is local-development only and one configured tenant owns the login.
Do not expose that local server or share a subscription as a hosted API service.
Model availability and quota depend on the user's plan. CLI token limits are
validated after completion; they cannot prevent subscription consumption that
has already occurred. Dollar ceilings require an API route instead.
Provider-side extra-usage billing settings can still incur charges; this adapter
does not change those settings or reconcile a subscription invoice.

## ChatGPT subscription: native local sign-in

On macOS/Linux, start iRoute's own authorization flow:

```bash
dotnet run --project src/iRoute.Runtime -- auth chatgpt login
dotnet run --project src/iRoute.Runtime -- auth chatgpt accounts
```

Open the printed URL and review the identity, renewable-session, and ChatGPT plan
permissions. The loopback callback listens only on `127.0.0.1`, for ten minutes.
OpenAI dynamically issues a client registration during first sign-in; there is
no Platform agent to create and no client secret to paste. iRoute verifies state,
PKCE, the ID-token signature, issuer, audience, expiry, nonce, and saved identity
before replacing account credentials. Granted token-response scopes, not callback
claims or a valid identity alone, determine whether plan access is available.
Eligibility and usage limits remain OpenAI-controlled. An operator-authorized
local sign-in and diagnostic requests exposed request/stream compatibility
defects, which are now corrected. The [October 1 local hardening benchmark](benchmarks/2026-10-01-hardening.md)
passed 32 further correctness cases across four non-Astra models through the
corrected adapter. Live inference is opt-in, not an automated CI check. This
does not establish availability for other accounts or production readiness.

Credentials are stored **unencrypted** in iRoute's own owner-only files, following
OpenAI's local-file guidance: directory `0700`, files `0600`, atomic replacements,
and an exclusive cross-process lease for rotating refresh. The location is
`<Environment.SpecialFolder.ApplicationData>/iroute/chatgpt/credentials.json`
(normally `~/.config/iroute/chatgpt/` on macOS/Linux). Do not share, commit, back up
in an unprotected location, or mount this directory into an untrusted runtime.
Owner-only file protection does not defend against processes running as the same
user. OS-keychain integration and Windows credential storage remain follow-up
work; Windows native sign-in fails closed rather than writing unprotected tokens.

Account commands preserve separate issued-client/identity records:

```bash
iroute auth chatgpt select <account-id>
iroute auth chatgpt login <account-id>   # same client and stable host ID
iroute auth chatgpt logout <account-id>  # revoke session; retain registration
```

Use the signed-in account in a loopback Development runtime:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ModelGateway__Mode=OpenAIChatGPT \
ModelGateway__Model='<model slug available to your ChatGPT account>' \
ModelGateway__ReasoningEffort=low \
  dotnet run --project src/iRoute.Runtime -- serve --urls http://127.0.0.1:8080
```

The active account is used by default; `ModelGateway__ChatGPTAccountId` (also
available per deployment) pins a saved account. ChatGPT requests use the public
Responses API with array-shaped `input`, `store=false`, and `stream=true`.
Text is assembled from indexed stream events when the terminal output is empty;
final snapshots must agree with streamed text. A completed terminal event and
valid usage are required before returning a buffered iRoute result. An absent
Content-Type is tolerated only for a valid Responses event stream, never as a
JSON/HTML fallback. Refusals, tool outputs, malformed/inconsistent events,
premature termination, and responses exceeding the 2 MiB safety bound are rejected.

ChatGPT plan requests omit `max_output_tokens`, which the
[preview does not support](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
For this adapter, iRoute's `MaxOutputTokens` is an **after-completion acceptance
limit**, not a provider-side spending or consumption cap. Deadline cancellation
and bounded response reading limit client work; they do not guarantee that
provider work or plan consumption stops. API-key adapters retain their native
output-token parameters. Use provider-side usage controls before increasing traffic.

Session refresh replaces
the access token, expiry, granted scopes, and rotating refresh token together.
Terminal refresh failures clear unusable tokens but retain registrations;
temporary failures preserve credentials. Sign-out attempts remote revocation
with bounded retries, clears local tokens, and warns if revocation was unconfirmed.

For compatibility, `ChatGPTAccessToken` or `IROUTE_CHATGPT_ACCESS_TOKEN` can
explicitly supply a token from iRoute's own plan-authorized flow; such an override
bypasses managed refresh and is not recommended for ordinary use. iRoute never
falls back to `OPENAI_API_KEY`, reads another app's credential files, or scrapes
browser sessions. Do not paste another application's token into iRoute.
See OpenAI's [registration](https://developers.openai.com/siwc/token-sharing-open-source/sign-in),
[session lifecycle](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions),
and [streaming inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
requirements. Manage plan and optional credit access in
[ChatGPT Settings → Usage](https://chatgpt.com/settings/usage); iRoute does not
change those settings or promise that provider usage is free.

All personal subscription routes are restricted to Development and their owner
tenant. Subscription usage is marked `costKnown=false`, dollar ceilings are
rejected, and routing cannot switch the first eligible route's billing type during
fallback, including when its circuit is already open. API keys and chat
subscriptions remain different access products.

## Verification

Health checks do not make billable provider calls; native adapters report a
degraded/unprobed state rather than claiming verified account/model health.
Run a deliberately small task with your own account to verify entitlement,
output, and usage before expanding traffic. Check provider usage independently.

Tests cover provider headers and payloads, structured results, token/cost
normalization, malformed/refused/truncated responses, bounded bodies, error
redaction, subscription tenancy, CLI safety flags, adapter selection, hosted
subscription rejection, and no silent subscription-to-API billing fallback.
OAuth tests cover callback rejection, ID-token validation, identity-only grants,
saved-account identity checks, token rotation, terminal/temporary refresh errors,
revocation, owner-only file modes, and cross-instance credential leases.

The [opt-in product benchmark](product-benchmark.md) exercises the full ChatGPT
pipeline and preserves real provider usage. Normal tests skip live calls.
Optional `usage.cachedInputTokens` and `usage.reasoningTokens` are subsets of
the existing input/output totals, not extra tokens to add. Missing details remain
null. Multi-step aggregation reports a subset only when all model steps provide
it; a partially known total is not presented as complete.
