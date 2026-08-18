# Verified version baseline — 1 August 2026

The executable baseline is the repository lock/configuration, not an aspirational version list.

| Component | Repository baseline | Verification policy |
|---|---|---|
| .NET SDK | minimum `10.0.100`; verified with `10.0.102` | latest installed .NET 10 feature band for container compatibility |
| .NET runtime | .NET 10; verified with `10.0.2` | update after build, test, and container checks |
| EF Core | `10.0.10` | keep Microsoft EF packages aligned |
| Npgsql EF | `10.0.3` | verify against both persistence providers |
| OpenTelemetry | `1.17.0` | telemetry export stays opt-in |

The canonical product release is `0.1.0-alpha.3` in `release.json`. Release
metadata, .NET package versions, Docker/Kubernetes tags, release notes, and the
changelog must remain aligned with the immutable Git tag.

Dependency updates merge only after build, contracts, evaluation, vulnerability, and compatibility checks.
