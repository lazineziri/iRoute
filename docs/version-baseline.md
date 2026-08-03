# Verified version baseline — 1 August 2026

The executable baseline is the repository lock/configuration, not an aspirational version list.

| Component | Repository baseline | Verification policy |
|---|---|---|
| .NET SDK | minimum `10.0.100`; verified with `10.0.102` | latest installed .NET 10 feature band for container compatibility |
| .NET runtime | .NET 10; verified with `10.0.2` | update after build, test, and container checks |
| EF Core | `10.0.10` | keep Microsoft EF packages aligned |
| Npgsql EF | `10.0.3` | verify against both persistence providers |
| OpenTelemetry | `1.17.0` | telemetry export stays opt-in |
| Node SDK | Node `24.18.1` is the supported floor, matching `release.json`, the root tooling, and the CI pin | test the supported Node line in CI before publishing |
| TypeScript | `7.0.2` | locked by `package-lock.json` |
| Python SDK | package minimum `3.12`; CI `3.14` | compile and conformance-test on the release CI baseline |
| Java SDK | Java `25` | compile with `--release 25`, `-Xlint:all`, and `-Werror` |
| PHP SDK | package minimum `8.3`; CI `8.5` | lint every source/example and run conformance in native CI |
| Rust SDK | Rust `1.97.1`, Edition 2024 | test the crate and check its example in native CI |
| AJV | `8.20.0` | contract validation; `$data` mode is not enabled |
| YAML | `2.9.0` | release/deployment manifest tests use checked repository inputs |

The canonical product release is `0.1.0-alpha.1` in `release.json`. Release
readiness fails if .NET, Node.js, Python, Java, Rust, Docker, Kubernetes, release
notes, or changelog versions diverge. PHP/Composer versions are assigned by the
immutable Git tag.

Dependency updates merge only after build, contracts, evaluation, vulnerability, and compatibility checks.
