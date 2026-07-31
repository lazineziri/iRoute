# Official SDK architecture

Official SDKs are thin clients generated from `spec/openapi/iroute.v1.yaml` and wrapped with a small idiomatic layer. They own authentication configuration, typed contracts, SSE consumption, cancellation, idempotency-safe retries, and error mapping. They do not own routing, task decomposition, prompts, memory, validation, or provider selection.

| SDK | Stable build baseline | Planned package |
|---|---|---|
| .NET | .NET 10 / C# 14 | `iRoute.Sdk` |
| Node.js | Node 24.18.1 LTS / TypeScript 7.0.2 | `@iroute/sdk` |
| Python | Python 3.14 | `iroute` |
| Java | Java 25 LTS | `dev.iroute:iroute-sdk` |
| PHP | PHP 8.5 | `iroute/sdk` |
| Rust | Rust 1.97.1 / Edition 2024 | `iroute-sdk` |

Each package must pass the same request, response, error, cancellation, streaming, and backward-compatibility fixtures before release.

## Implementation status

- `.NET`: typed execution, lookup, cancellation, artifact retrieval, tenant/actor scope, idempotency, and SSE consumption are implemented in `src/iRoute.Sdk.DotNet`.
- `Node.js`: the same runtime surface is implemented and type-checked in `sdks/node`.
- Python, Java, PHP, and Rust directories currently hold package boundaries only; they are not published or represented as functional clients.
