# iRoute Rust SDK

`iroute-sdk` is a dependency-free Rust client for the iRoute v1 HTTP and SSE
API. It returns JSON strings and lets applications choose their JSON and HTTPS
stacks. It supports execution, polling, cancellation, approvals, artifacts,
gateway health, observability, and buffered event replay.

Routing, provider selection, validation, workflow retries, and fallback remain
server-side.

## Status and requirements

- SDK version: `0.1.0-alpha.3`
- Rust baseline: `1.97.1`
- Edition: 2024
- Crate name: `iroute-sdk`
- Runtime dependencies: none

The public alpha is published on crates.io. Cargo does not select prereleases
automatically, so request the exact version. The source installation below remains available.

## Start iRoute

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default API URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from crates.io

```bash
cargo add iroute-sdk@0.1.0-alpha.3
```

## Install from source

Add the SDK checkout to the application's `Cargo.toml`:

```toml
[dependencies]
iroute-sdk = { path = "../iRoute/sdks/rust" }
```

Add `serde_json` in the application if typed JSON parsing is desired. The SDK
itself deliberately has no JSON dependency.

## Create a client

```rust
use iroute_sdk::{ClientOptions, IRouteClient};
use std::env;

let client = IRouteClient::new(
    env::var("IROUTE_URL").unwrap_or_else(|_| "http://localhost:8080".into()),
    ClientOptions {
        token: env::var("IROUTE_TOKEN").ok(),
        tenant_id: Some(env::var("IROUTE_TENANT").unwrap_or_else(|_| "demo".into())),
        actor_id: Some(env::var("IROUTE_ACTOR").unwrap_or_else(|_| "rust-app".into())),
        permission_scopes: Vec::new(),
    },
);
```

In Development the identity options become headers. In JWT mode the server
derives identity and scopes from token claims and will not allow headers to
elevate them.

## HTTP and HTTPS transport

The built-in `HttpTransport` supports only `http://` and is intended for the
local quick start. It uses blocking `TcpStream` I/O with a 30-second default
read/write timeout. It does not provide TLS.

Production HTTPS applications must inject a transport backed by their preferred
HTTP/TLS library through `IRouteClient::with_transport`. The transport receives
an `IRouteRequest` and returns an `IRouteResponse`; SDK header construction and
problem-detail mapping remain unchanged.

## Submit an execution

```rust
use std::time::{SystemTime, UNIX_EPOCH};

let suffix = SystemTime::now()
    .duration_since(UNIX_EPOCH)?
    .as_nanos();
let idempotency_key = format!("rust-email-{suffix}");
let request = format!(
    r#"{{
      "taskType":"email.draft",
      "input":{{"purpose":"Confirm the Rust SDK integration."}},
      "idempotencyKey":"{idempotency_key}",
      "constraints":{{
        "maxOutputTokens":500,
        "deadlineMilliseconds":30000,
        "minimumQuality":0.8
      }}
    }}"#
);

let execution_json = client.execute_json(&request, Some(&idempotency_key))?;
println!("{execution_json}");
```

The body and header idempotency keys must agree. Keep the same key when retrying
the same logical request.

## Poll until terminal

The SDK returns complete JSON strings. Parse them with the application's chosen
library. With `serde_json`:

```rust
use serde_json::Value;
use std::thread;
use std::time::Duration;

let mut execution: Value = serde_json::from_str(&execution_json)?;
let execution_id = execution["executionId"]
    .as_str()
    .ok_or("missing executionId")?
    .to_owned();
let terminal = ["Succeeded", "Failed", "Cancelled", "TimedOut"];

while !terminal.contains(&execution["status"].as_str().unwrap_or("")) {
    thread::sleep(Duration::from_millis(500));
    let current = client
        .get_json(&execution_id)?
        .ok_or("execution is not visible to this tenant")?;
    execution = serde_json::from_str(&current)?;
}

if execution["status"] == "Succeeded" {
    println!("{}", execution["outcome"]["output"]);
} else {
    eprintln!("{}", execution["error"]);
}
```

`WaitingForApproval` is non-terminal.

## Read event replay

```rust
let mut last_sequence = 0_u64;

for event_json in client.stream_events_json(&execution_id, last_sequence)? {
    let event: serde_json::Value = serde_json::from_str(&event_json)?;
    last_sequence = event["sequence"].as_u64().unwrap_or(last_sequence);
    println!("{} {}", last_sequence, event["type"]);
}
```

The built-in transport buffers the HTTP response, and `stream_events_json`
returns `Vec<String>` after the stream closes. It is suitable for terminal replay
and bounded local use, not an indefinitely open live feed. Applications needing
incremental SSE should provide a streaming HTTPS client. Persist the sequence
for reconnects and ignore unknown event fields.

## Cancel an execution

```rust
let accepted = client.cancel(&execution_id)?;
if !accepted {
    println!("Execution was not found.");
}
```

Poll or replay events to confirm the durable terminal result.

## Approve or deny an action

Read `actionId` from an `approval.required` event:

```rust
let decision = format!(
    r#"{{"actionId":"{action_id}","approved":true,"reason":"Reviewed by an operator"}}"#
);
let result_json = client.submit_approval_json(&execution_id, &decision)?;
```

The deciding token needs `approval:grant` and the action's required scope.

## Retrieve an artifact

```rust
if let Some(artifact_json) = client.get_artifact_json(&artifact_id)? {
    println!("{artifact_json}");
}
```

Not found returns `None`, including for cross-tenant access.

## Health and observability

```rust
use std::collections::BTreeMap;

let health_json = client.get_model_gateway_health_json()?;

let query = BTreeMap::from([
    ("from".into(), "2026-08-01T00:00:00Z".into()),
    ("to".into(), "2026-08-02T00:00:00Z".into()),
    ("taskType".into(), "email.draft".into()),
]);
let summary_json = client.get_observability_summary_json(&query)?;
let timeline_json = client.get_execution_timeline_json(&execution_id)?;
```

Gateway health returns JSON for HTTP `200` and `503`. Summary query keys are
`from`, `to`, `taskType`, and `policyVersion`. Observability is tenant scoped,
and reported cost has no currency in v1.

## Errors and timeouts

```rust
use iroute_sdk::IRouteError;

match client.execute_json(&request, Some(&idempotency_key)) {
    Ok(json) => println!("{json}"),
    Err(IRouteError::Api(error)) => {
        eprintln!("HTTP {} {:?}: {:?}", error.status, error.code, error.detail);
        eprintln!("{}", error.response_body);
    }
    Err(IRouteError::Transport(message)) => eprintln!("transport: {message}"),
}
```

To change the built-in transport timeout, construct
`HttpTransport::new(Duration)` and pass it to `with_transport`. The SDK does not
retry automatically.

## Method reference

| Method | Result |
|---|---|
| `execute_json(request, idempotency_key)` | Execution JSON string |
| `get_json(execution_id)` | Optional execution JSON |
| `cancel(execution_id)` | `false` when not found |
| `submit_approval_json(execution_id, decision)` | Approval result JSON |
| `stream_events_json(execution_id, after_sequence)` | Buffered event JSON vector |
| `get_artifact_json(artifact_id)` | Optional artifact JSON |
| `get_model_gateway_health_json()` | Health JSON |
| `get_observability_summary_json(query)` | Summary JSON |
| `get_execution_timeline_json(execution_id)` | Optional timeline JSON |

## Run the example and tests

From the repository root:

```bash
cargo run --manifest-path sdks/rust/Cargo.toml --example execute
cargo test --manifest-path sdks/rust/Cargo.toml
```

The checked example is [examples/execute.rs](examples/execute.rs). Public wire
shapes are defined by the
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
