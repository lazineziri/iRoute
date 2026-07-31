# Verified version baseline — 31 July 2026

The executable baseline is the repository lock/configuration, not an aspirational version list.

| Component | Repository baseline | Verification policy |
|---|---|---|
| .NET SDK | minimum `10.0.100`; verified with `10.0.102` | latest installed .NET 10 feature band for container compatibility |
| .NET runtime | .NET 10; verified with `10.0.2` | update after build, test, and container checks |
| EF Core | `10.0.10` | keep Microsoft EF packages aligned |
| Npgsql EF | `10.0.3` | verify against both persistence providers |
| OpenTelemetry | `1.17.0` | telemetry export stays opt-in |
| Node SDK | package target Node `24.18.1`; type-check also verified on Node `22.20.0` | test supported Node lines in CI before publishing |
| TypeScript | `7.0.2` | locked by `package-lock.json` |

Dependency updates merge only after build, contracts, evaluation, vulnerability, and compatibility checks.
