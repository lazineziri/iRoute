# Security policy

Security reports are handled privately. Do not disclose a suspected
vulnerability in a public issue, pull request, discussion, evaluation fixture,
or chat transcript.

## Reporting a vulnerability

Use the repository's **Security** tab and select **Report a vulnerability** to
open a private GitHub security advisory. This is the canonical reporting
channel and does not require a public issue.

Include, when available:

- affected version or commit;
- deployment and storage profile;
- impact and required privileges;
- minimal reproduction steps;
- whether tenant isolation, authorization, secrets, stored state, or external
  side effects are involved;
- suggested mitigation, without including real credentials or customer data.

If private advisories are unavailable on a downstream mirror, contact that
mirror's owner privately. Do not use community conduct-reporting channels for a
technical vulnerability.

## What to expect

Maintainers will acknowledge a complete report as capacity permits, validate
the impact privately, coordinate a fix and advisory, and credit the reporter if
requested and appropriate. This community project does not promise a production
SLA, security-response SLA, or remediation SLA. Please allow maintainers to
publish a fix before public disclosure; coordinated timelines will reflect
severity and exploitability.

## Supported versions

| Version | Security fixes |
|---|---|
| `0.1.0-alpha.3` | Current prerelease; best-effort fixes |
| Earlier commits and untagged builds | Not supported |

Until a stable release, only the latest tagged prerelease receives security
fixes. Upgrading may require following the documented migration procedure.

## Security boundary

iRoute does not intentionally export prompts, outputs, credentials, tenant
identifiers, or telemetry by default. `DevelopmentHeaders` is rejected at API
startup unless the host environment is exactly `Development`. External actions must be capability-
allow-listed, authenticated, authorized, approval-gated where configured,
idempotent, and auditable. Development identity headers and deterministic
connectors are local reference modes, not internet-facing production controls.

The following are generally in scope:

- cross-tenant reads, writes, reuse, or observability;
- authentication or permission-scope bypass;
- approval, idempotency, or external-action bypass;
- prompt/context exposure through logs, events, traces, or dashboards;
- arbitrary connector destination, SQL, operation, MCP tool, or provider access;
- unsafe deserialization, injection, denial of service, or secret disclosure;
- migration or lifecycle behavior that causes unauthorized data loss.

The following are generally out of scope unless they demonstrate a concrete
security boundary failure:

- expected behavior in `DevelopmentHeaders` mode on a trusted local machine;
- quality disagreements with deterministic reference adapters;
- unavailable third-party providers;
- vulnerabilities only in unsupported versions;
- automated scan output without a reproducible impact.

## Safe research

Use only systems and data you own or have explicit permission to test. Avoid
privacy violations, service disruption, persistence beyond the minimum needed
for validation, and access to other tenants. Stop if testing reaches real
customer data or secrets. Good-faith research consistent with this policy will
not be intentionally pursued by the project as an abuse report.

Never commit model keys, connector credentials, personal data, production
traces, private advisories, or unredacted evaluation fixtures.
