# SDK quick starts

Start the complete local API and execution worker from the repository root:

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

For a source run, start these in separate terminals:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Api -- --urls http://localhost:8080
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Worker
```

Every example submits the same `email.draft` task to that runtime. No model-provider
credential is required by the deterministic development profile. Set `IROUTE_URL`,
`IROUTE_TOKEN`, `IROUTE_TENANT`, or `IROUTE_ACTOR` to override the local defaults.

| Client | Run from the repository root | Complete guide |
|---|---|---|
| CLI | `dotnet run --project src/iRoute.Cli -- execute --request @examples/email-draft.json --idempotency-key cli-example-001` | Root [README](../../README.md) |
| .NET | `dotnet run --project examples/sdks/dotnet` | [.NET](../../src/iRoute.Sdk.DotNet/README.md) |
| Node.js | `npm run build --prefix sdks/node && node examples/sdks/node/execute.mjs` | [Node.js](../../sdks/node/README.md) |
| Python | `PYTHONPATH=sdks/python/src python3 examples/sdks/python/execute.py` | [Python](../../sdks/python/README.md) |
| Java | `cd sdks/java && ./run-example.sh` | [Java](../../sdks/java/README.md) |
| PHP | `composer install --working-dir=sdks/php && php examples/sdks/php/execute.php` | [PHP](../../sdks/php/README.md) |
| Rust | `cargo run --manifest-path sdks/rust/Cargo.toml --example execute` | [Rust](../../sdks/rust/README.md) |

The SDKs only serialize the public protocol, apply authentication and scope headers,
consume SSE, and map errors. Routing and provider selection remain server-side.
Submission normally returns `Queued`; keep the worker running, then poll or
consume events until the execution is terminal.
