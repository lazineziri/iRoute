# Support

iRoute is an early community prerelease. Support is best effort and carries no
response-time, availability, compatibility, or remediation SLA.

- Use a bug report for a reproducible defect in a supported version.
- Use a feature request for a proposed product or protocol change.
- Use repository discussions, when enabled, for questions and design ideas.
- Use a private security advisory for vulnerabilities; see [SECURITY.md](SECURITY.md).

Include the iRoute version, operating system/architecture, storage profile,
deployment profile, minimal configuration with secrets removed, reproduction,
expected result, and actual result. Do not attach production databases, prompts,
outputs, tokens, credentials, or unredacted traces.

The current support and compatibility boundaries are documented in
[docs/compatibility.md](docs/compatibility.md). Production operators remain
responsible for identity, PostgreSQL availability/backups, ingress/TLS, secret
management, the external model gateway, provider contracts, and restoration
testing.
