# iRoute Python SDK

`iroute` is a dependency-light synchronous Python client for the iRoute v1 HTTP
and SSE API. It returns decoded dictionaries, consumes events incrementally, and
supports execution, polling, cancellation, approvals, artifacts, gateway health,
and observability.

Routing, provider selection, validation, workflow retries, and fallback remain
server-side.

## Status and requirements

- SDK version: `0.1.0a2`
- Minimum Python: `3.12`
- Verified baseline: Python `3.14`
- Runtime dependencies: standard library only
- Package name: `iroute`

Once published, the public alpha is installed from PyPI as a prerelease
package. The source installation below remains available.

## Start iRoute

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default API URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from PyPI

```bash
python -m pip install --pre iroute==0.1.0a2
```

## Install from source

Use an isolated environment and install the local SDK:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -e ../iRoute/sdks/python
```

For a checkout-only quick start, set `PYTHONPATH=../iRoute/sdks/python/src`
instead of installing.

## Create a client

```python
import os

from iroute import IRouteClient

client = IRouteClient(
    os.getenv("IROUTE_URL", "http://localhost:8080"),
    token=os.getenv("IROUTE_TOKEN"),
    tenant_id=os.getenv("IROUTE_TENANT", "demo"),
    actor_id=os.getenv("IROUTE_ACTOR", "python-app"),
    permission_scopes=(),
    timeout=30,
)
```

In Development the identity options become headers. In JWT mode the server
derives identity and scopes from token claims and will not allow headers to
elevate them. `opener` can be replaced with a compatible callable for tests or a
custom transport policy.

## Submit an execution

```python
import uuid

request = {
    "taskType": "email.draft",
    "input": {"purpose": "Confirm the Python SDK integration."},
    "idempotencyKey": f"python-email-{uuid.uuid4()}",
    "constraints": {
        "maxOutputTokens": 500,
        "deadlineMilliseconds": 30_000,
        "minimumQuality": 0.8,
    },
}

execution = client.execute(request)

print(execution["executionId"], execution["status"])
```

The shorthand form is also available when no other request fields are needed:

```python
execution = client.execute(
    "email.draft",
    {"purpose": "Confirm the Python SDK integration."},
)
```

Prefer the dictionary form so you can supply a stable idempotency key and
constraints.

## Poll until terminal

```python
import time

terminal = {"Succeeded", "Failed", "Cancelled", "TimedOut"}

while execution["status"] not in terminal:
    time.sleep(0.5)
    current = client.get(execution["executionId"])
    if current is None:
        raise RuntimeError("Execution is not visible to this tenant.")
    execution = current

if execution["status"] == "Succeeded":
    print(execution["outcome"]["output"])
else:
    error = execution.get("error") or {}
    print(error.get("code"), error.get("detail"))
```

`WaitingForApproval` is non-terminal.

## Stream and reconnect to events

`stream_events` reads the response incrementally:

```python
last_sequence = 0

for event in client.stream_events(execution["executionId"], last_sequence):
    print(event["sequence"], event["type"])
    last_sequence = event["sequence"]
```

Persist the last processed sequence and pass it back after reconnecting. Ignore
unknown event types and fields.

The iterator occupies the calling thread while the connection is open. Run it
in an application-managed worker thread if the rest of the application must
remain responsive.

## Cancel an execution

```python
accepted = client.cancel(execution["executionId"])
if not accepted:
    print("Execution was not found.")
```

Cancellation is durable but cooperative. Poll or reconnect to events to confirm
the terminal result.

## Approve or deny an action

Read `actionId` from an `approval.required` event:

```python
result = client.submit_approval(execution["executionId"], {
    "actionId": action_id,
    "approved": True,
    "reason": "Reviewed by an authorized operator",
})

execution = result["execution"]
```

The deciding token needs `approval:grant` and the action's required scope.

## Retrieve an artifact

```python
artifacts = (execution.get("outcome") or {}).get("artifacts", [])
if artifacts:
    artifact = client.get_artifact(artifacts[0]["artifactId"])
    print(artifact["content"] if artifact else "not found")
```

Not found returns `None`, including for cross-tenant access.

## Health and observability

```python
from datetime import datetime, timedelta, timezone

health = client.get_model_gateway_health()
print(health["gatewayId"], health["status"])

now = datetime.now(timezone.utc)
summary = client.get_observability_summary(
    from_time=(now - timedelta(hours=24)).isoformat(),
    to_time=now.isoformat(),
    task_type="email.draft",
)
print(summary["totals"]["executions"])

timeline = client.get_execution_timeline(execution["executionId"])
print(timeline["traceId"] if timeline else "not found")
```

Gateway health returns a decoded body for HTTP `200` and `503`. Observability is
tenant scoped. Reported cost has no currency in v1.

## Errors and timeouts

```python
from iroute import IRouteApiError

try:
    execution = client.execute(request)
except IRouteApiError as error:
    print(error.status, error.code, error.detail)
    print(error.response_body)
```

Transport timeout is configured in the client constructor. `timeout` (default 30
seconds) applies to ordinary requests. `stream_events` uses `stream_timeout`,
which defaults to `None` and waits indefinitely: the timeout applies to each
socket read, and an execution can legitimately produce no events for minutes,
so reusing the request timeout would abort a healthy stream. Set
`stream_timeout` to a number to bound it. Standard-library network exceptions
remain transport exceptions. The SDK does not automatically retry. Retrying a
submission requires the same idempotency key.

## Method reference

| Method | Result |
|---|---|
| `execute(request)` | Execution dictionary |
| `execute(task_type, input_data)` | Shorthand execution dictionary |
| `get(execution_id)` | Dictionary or `None` |
| `cancel(execution_id)` | `False` when not found |
| `submit_approval(execution_id, decision)` | Approval result dictionary |
| `stream_events(execution_id, after_sequence=0)` | Incremental iterator |
| `get_artifact(artifact_id)` | Dictionary or `None` |
| `get_model_gateway_health()` | Health dictionary |
| `get_observability_summary(...)` | Summary dictionary |
| `get_execution_timeline(execution_id)` | Timeline or `None` |

## Run the example and tests

From the repository root:

```bash
PYTHONPATH=sdks/python/src python3 examples/sdks/python/execute.py
PYTHONPATH=sdks/python/src python3 -m unittest discover -s sdks/python/tests
```

Public wire shapes are defined by the
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
