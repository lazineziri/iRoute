# SDK quick starts

Start the local runtime from the repository root:

```bash
dotnet run --project src/iRoute.Api -- --urls http://localhost:8080
```

Every example submits the same `email.draft` task to that runtime. No model-provider
credential is required by the deterministic development profile. Set `IROUTE_URL`,
`IROUTE_TOKEN`, `IROUTE_TENANT`, or `IROUTE_ACTOR` to override the local defaults.

| Client | Run from the repository root |
|---|---|
| CLI | `dotnet run --project src/iRoute.Cli -- execute --request @examples/email-draft.json --idempotency-key cli-example-001` |
| .NET | `dotnet run --project examples/sdks/dotnet` |
| Node.js | `npm run build --prefix sdks/node && node examples/sdks/node/execute.mjs` |
| Python | `PYTHONPATH=sdks/python/src python3 examples/sdks/python/execute.py` |
| Java | `cd sdks/java && ./run-example.sh` |
| PHP | `composer install --working-dir=sdks/php && php examples/sdks/php/execute.php` |
| Rust | `cargo run --manifest-path sdks/rust/Cargo.toml --example execute` |

The SDKs only serialize the public protocol, apply authentication and scope headers,
consume SSE, and map errors. Routing and provider selection remain server-side.
