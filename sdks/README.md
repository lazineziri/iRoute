# Official SDK architecture

Official SDKs are thin protocol clients aligned with `spec/openapi/iroute.v1.yaml` and wrapped with a small idiomatic layer. They own authentication configuration, public contracts, SSE consumption, cancellation, idempotency propagation, and error mapping. They do not own routing, task decomposition, prompts, memory, validation, retry policy, or provider selection.

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

- `.NET`: typed public contracts and async streaming in `src/iRoute.Sdk.DotNet`.
- `Node.js`: typed TypeScript contracts with injectable Fetch transport in `sdks/node`.
- `Python`: standard-library client with an injectable URL opener in `sdks/python`.
- `Java`: JDK HTTP client with an injectable transport in `sdks/java`.
- `PHP`: cURL client with an injectable callable transport in `sdks/php`.
- `Rust`: dependency-free local HTTP client with an injectable transport for TLS or application-specific stacks in `sdks/rust`.

All six clients implement execution, lookup, cancellation, approval, artifact, model-gateway health, observability summary/timeline, SSE, and typed API-error semantics. They consume the identical fixtures in `conformance/v1.properties`. Run every locally installed toolchain with:

```bash
npm run test:sdks
```

See the runnable [reference quick starts](../examples/sdks/README.md) and the `iroute` CLI in `src/iRoute.Cli`.
