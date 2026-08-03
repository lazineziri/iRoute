# iRoute Java SDK

`dev.iroute:iroute-sdk` is a dependency-free Java client for the iRoute v1 HTTP
and SSE API. It uses the JDK HTTP client and deliberately returns JSON strings so
applications can choose their own JSON library.

It supports execution, polling, cancellation, approvals, artifacts, gateway
health, observability, and buffered event replay. Routing and provider policy
remain server-side.

## Status and requirements

- SDK version: `0.1.0-alpha.2`
- Java baseline: Java 25 LTS
- Maven coordinates: `dev.iroute:iroute-sdk`
- Runtime dependencies: JDK only

Once published, the public alpha is installed from Maven Central as a
prerelease artifact. The source installation below remains available.

## Start iRoute

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default API URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from Maven Central

```xml
<dependency>
  <groupId>dev.iroute</groupId>
  <artifactId>iroute-sdk</artifactId>
  <version>0.1.0-alpha.2</version>
</dependency>
```

## Install from source

Build and install the SDK into the local Maven repository:

```bash
mvn --file ../iRoute/sdks/java/pom.xml clean install
```

Then add it to the application:

```xml
<dependency>
  <groupId>dev.iroute</groupId>
  <artifactId>iroute-sdk</artifactId>
  <version>0.1.0-alpha.2</version>
</dependency>
```

## Create a client

```java
import dev.iroute.IRouteClient;
import java.net.URI;
import java.time.Duration;
import java.util.List;
import java.util.Map;

var environment = System.getenv();
var client = new IRouteClient(
    URI.create(environment.getOrDefault("IROUTE_URL", "http://localhost:8080")),
    new IRouteClient.Options(
        environment.get("IROUTE_TOKEN"),
        environment.getOrDefault("IROUTE_TENANT", "demo"),
        environment.getOrDefault("IROUTE_ACTOR", "java-app"),
        List.of(),
        Duration.ofSeconds(30)));
```

In Development the identity options become headers. In JWT mode the server
derives identity and scopes from token claims and will not allow headers to
elevate them.

## JSON representation

The SDK does not impose Jackson, Gson, JSON-P, or another JSON dependency.
Methods ending in `Json` accept or return complete JSON strings. The examples
below use `json.readTree(...)` to mean the parser already chosen by the
application. With Jackson, `json` would be an `ObjectMapper`.

## Submit an execution

```java
import java.util.UUID;

var idempotencyKey = "java-email-" + UUID.randomUUID();
var request = """
    {
      "taskType": "email.draft",
      "input": {"purpose": "Confirm the Java SDK integration."},
      "idempotencyKey": "%s",
      "constraints": {
        "maxOutputTokens": 500,
        "deadlineMilliseconds": 30000,
        "minimumQuality": 0.8
      }
    }
    """.formatted(idempotencyKey);

var executionJson = client.executeJson(request, idempotencyKey);
System.out.println(executionJson);
```

Keep the same idempotency key when retrying the same logical request.

## Poll until terminal

Parse `executionId` and `status` with the application's JSON library:

```java
var execution = json.readTree(executionJson);
var executionId = execution.required("executionId").asText();
var terminal = java.util.Set.of("Succeeded", "Failed", "Cancelled", "TimedOut");

while (!terminal.contains(execution.required("status").asText())) {
    Thread.sleep(500);
    var current = client.getJson(executionId)
        .orElseThrow(() -> new IllegalStateException(
            "Execution is not visible to this tenant."));
    execution = json.readTree(current);
}

if (execution.required("status").asText().equals("Succeeded")) {
    System.out.println(execution.required("outcome").required("output"));
} else {
    System.err.println(execution.path("error"));
}
```

`WaitingForApproval` is non-terminal.

## Read event replay

```java
long lastSequence = 0;
var eventJsonValues = client.streamEventsJson(executionId, lastSequence);

for (var eventJson : eventJsonValues) {
    var event = json.readTree(eventJson);
    lastSequence = event.required("sequence").asLong();
    System.out.println(lastSequence + " " + event.required("type").asText());
}
```

The default Java transport uses `BodyHandlers.ofString()`. It buffers the server
response and returns `List<String>` after the stream closes; it is suitable for
terminal replay and bounded local use, not an indefinitely open live feed.
Applications requiring incremental events should provide an incremental
transport or consume the SSE endpoint with their HTTP stack. Persist the last
sequence for reconnects and ignore unknown event fields.

## Cancel an execution

```java
var accepted = client.cancel(executionId);
if (!accepted) System.out.println("Execution was not found.");
```

Poll or replay events to confirm the durable terminal result.

## Approve or deny an action

Read `actionId` from an `approval.required` event:

```java
var decision = """
    {"actionId":"%s","approved":true,"reason":"Reviewed by an operator"}
    """.formatted(actionId);

var approvalResultJson = client.submitApprovalJson(executionId, decision);
```

The deciding token needs `approval:grant` and the action's required scope.

## Retrieve an artifact

```java
var artifactJson = client.getArtifactJson(artifactId);
artifactJson.ifPresent(System.out::println);
```

Not found returns `Optional.empty()`, including for cross-tenant access.

## Health and observability

```java
var healthJson = client.getModelGatewayHealthJson();

var summaryJson = client.getObservabilitySummaryJson(Map.of(
    "from", "2026-08-01T00:00:00Z",
    "to", "2026-08-02T00:00:00Z",
    "taskType", "email.draft"));

var timelineJson = client.getExecutionTimelineJson(executionId);
```

Gateway health returns JSON for HTTP `200` and `503`. Summary query keys are
`from`, `to`, `taskType`, and `policyVersion`. Observability is tenant scoped,
and reported cost has no currency in v1.

## Errors and transport timeouts

```java
try {
    executionJson = client.executeJson(request, idempotencyKey);
} catch (IRouteClient.IRouteApiException error) {
    System.err.println(error.status() + " " + error.code());
    System.err.println(error.detail());
    System.err.println(error.responseBody());
}
```

`IOException` covers transport failures and `InterruptedException` preserves
thread interruption. `Options.timeout` (default 30 seconds) bounds an ordinary
request, and `Options.streamTimeout` (default 15 minutes) bounds
`streamEventsJson`, which buffers a replay and stays open until the execution is
terminal. Both bound the complete exchange, not just the response headers, so a
stalled stream raises `IOException` instead of blocking forever. The SDK does not
retry automatically.

## Custom transport

The constructor overload accepting `IRouteClient.Transport` supports tests,
custom TLS configuration, proxies, metrics, or another HTTP stack. A transport
receives an immutable `IRouteClient.Request` and must return an
`IRouteClient.Response`; SDK error mapping remains unchanged.

## Method reference

| Method | Result |
|---|---|
| `executeJson(requestJson, idempotencyKey?)` | Execution JSON |
| `getJson(executionId)` | Optional execution JSON |
| `cancel(executionId)` | `false` when not found |
| `submitApprovalJson(executionId, decisionJson)` | Approval result JSON |
| `streamEventsJson(executionId, afterSequence)` | Buffered event JSON list |
| `getArtifactJson(artifactId)` | Optional artifact JSON |
| `getModelGatewayHealthJson()` | Health JSON |
| `getObservabilitySummaryJson(query)` | Summary JSON |
| `getExecutionTimelineJson(executionId)` | Optional timeline JSON |

## Run the example and tests

From the repository root:

```bash
cd sdks/java
./run-example.sh
./run-conformance.sh
```

Public wire shapes are defined by the
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
