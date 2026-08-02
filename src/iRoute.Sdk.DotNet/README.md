# iRoute .NET SDK

`iRoute.Sdk` is the typed .NET client for the iRoute v1 HTTP and SSE API. It
uses the public contracts from `iRoute.Contracts` and supports asynchronous
execution, polling, event streaming, cancellation, approvals, artifacts,
gateway health, and observability.

Routing, provider selection, memory, validation, workflow retries, and fallback
remain server-side.

## Status and requirements

- SDK version: `0.1.0-alpha.1`
- Verified baseline: .NET 10 / C# 14
- Package ID: `iRoute.Sdk`
- License: Apache-2.0

Once published, the public alpha is installed from NuGet as a prerelease
package. The source installation below remains available.

## Start iRoute

From the repository root, start the complete local API and worker:

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from NuGet

```bash
dotnet add package iRoute.Sdk --version 0.1.0-alpha.1
```

## Install from source

Reference the SDK project from your application:

```bash
dotnet add MyApp.csproj reference ../iRoute/src/iRoute.Sdk.DotNet/iRoute.Sdk.DotNet.csproj
```

Or build and consume a local package:

```bash
dotnet pack ../iRoute/src/iRoute.Sdk.DotNet \
  --configuration Release \
  --output ../packages
dotnet add MyApp.csproj package iRoute.Sdk \
  --version 0.1.0-alpha.1 \
  --source ../packages
```

Adjust paths for your checkout.

## Create a client

```csharp
using iRoute.Sdk.DotNet;

using var http = new HttpClient
{
    BaseAddress = new Uri(
        Environment.GetEnvironmentVariable("IROUTE_URL")
        ?? "http://localhost:8080"),
    Timeout = TimeSpan.FromSeconds(30)
};

var client = new IRouteClient(http, new IRouteClientOptions(
    TenantId: Environment.GetEnvironmentVariable("IROUTE_TENANT") ?? "demo",
    ActorId: Environment.GetEnvironmentVariable("IROUTE_ACTOR") ?? "dotnet-app",
    PermissionScopes: [],
    BearerToken: Environment.GetEnvironmentVariable("IROUTE_TOKEN")));
```

In Development, tenant, actor, and permission options become headers. In JWT
mode the server derives identity and scopes from token claims and will not allow
headers to elevate them.

## Submit an execution

```csharp
using System.Text.Json;
using iRoute.Contracts;

using var input = JsonDocument.Parse("""
    {"purpose":"Confirm the .NET SDK integration."}
    """);

var request = new TaskRequest(
    TaskType: "email.draft",
    Input: input.RootElement.Clone(),
    IdempotencyKey: "dotnet-email-001",
    Constraints: new TaskConstraints(
        MaxOutputTokens: 500,
        DeadlineMilliseconds: 30_000,
        MinimumQuality: 0.8m));

var snapshot = await client.ExecuteAsync(request);

Console.WriteLine($"{snapshot.ExecutionId}: {snapshot.Status}");
```

Keep the same idempotency key when retrying the same logical request. A queued
submission normally returns immediately with HTTP `202`.

## Poll until terminal

The SDK intentionally leaves polling policy to the application:

```csharp
static bool IsTerminal(ExecutionStatus status) => status is
    ExecutionStatus.Succeeded or
    ExecutionStatus.Failed or
    ExecutionStatus.Cancelled or
    ExecutionStatus.TimedOut;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));

while (!IsTerminal(snapshot.Status))
{
    await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
    snapshot = await client.GetAsync(snapshot.ExecutionId, deadline.Token)
        ?? throw new InvalidOperationException("Execution is not visible to this tenant.");
}

if (snapshot.Status == ExecutionStatus.Succeeded)
{
    Console.WriteLine(snapshot.Outcome!.Output);
}
else
{
    Console.Error.WriteLine($"{snapshot.Error?.Code}: {snapshot.Error?.Detail}");
}
```

`WaitingForApproval` is non-terminal and requires a decision before processing
can resume.

## Stream and reconnect to events

`StreamEventsAsync` reads SSE incrementally. Persist the last processed sequence
and pass it back when reconnecting:

```csharp
long lastSequence = 0;

await foreach (var executionEvent in client.StreamEventsAsync(
    snapshot.ExecutionId,
    afterSequence: lastSequence,
    cancellationToken: deadline.Token))
{
    Console.WriteLine($"{executionEvent.Sequence} {executionEvent.Type}");
    lastSequence = executionEvent.Sequence;
}
```

Unknown event types and fields must be ignored. A terminal execution closes its
replay stream after all newer events are sent.

## Cancel an execution

```csharp
var accepted = await client.CancelAsync(snapshot.ExecutionId);
if (!accepted)
{
    Console.WriteLine("Execution was not found.");
}
```

Cancellation is durable but cooperative. A running worker observes it through
its heartbeat; a later snapshot confirms the terminal state.

## Approve or deny an action

Read the durable `actionId` from an `approval.required` event, then submit a
decision:

```csharp
var result = await client.SubmitApprovalAsync(
    snapshot.ExecutionId,
    new ApprovalDecision(
        ActionId: actionId,
        Approved: true,
        Reason: "Reviewed by the operator"));

snapshot = result.Execution;
```

The deciding identity needs `approval:grant` and the action's required scope.
Never invent or reuse an action ID from another execution.

## Retrieve an artifact

```csharp
var artifactReference = snapshot.Outcome?.Artifacts.FirstOrDefault();
if (artifactReference is not null)
{
    var artifact = await client.GetArtifactAsync(artifactReference.ArtifactId);
    Console.WriteLine(artifact?.Content);
}
```

Not found returns `null`, including for cross-tenant access.

## Health and observability

```csharp
var gateway = await client.GetModelGatewayHealthAsync();
Console.WriteLine($"{gateway.GatewayId}: {gateway.Status}");

var summary = await client.GetObservabilitySummaryAsync(
    new ObservabilityQueryOptions(
        From: DateTimeOffset.UtcNow.AddHours(-24),
        To: DateTimeOffset.UtcNow,
        TaskType: "email.draft"));
Console.WriteLine($"observed={summary.Totals.Executions}");

var timeline = await client.GetExecutionTimelineAsync(snapshot.ExecutionId);
Console.WriteLine(timeline?.TraceId);
```

Gateway health returns a typed body for both HTTP `200` and `503`. Observability
is tenant scoped. Reported cost has no currency in the v1 contract.

## Errors, timeouts, and cancellation

HTTP problem details are mapped to `IRouteApiException`:

```csharp
try
{
    snapshot = await client.ExecuteAsync(request, deadline.Token);
}
catch (IRouteApiException error)
{
    var status = error.StatusCode is { } statusCode ? (int)statusCode : 0;
    Console.Error.WriteLine(
        $"HTTP {status} {error.Code}: {error.Detail}");
    Console.Error.WriteLine(error.ResponseBody);
}
catch (OperationCanceledException) when (deadline.IsCancellationRequested)
{
    Console.Error.WriteLine("Client deadline elapsed.");
}
```

Configure transport timeout on `HttpClient` and operation cancellation with a
`CancellationToken`. The SDK does not automatically retry. Retrying a submission
requires the same idempotency key.

## Method reference

| Method | Result |
|---|---|
| `ExecuteAsync(TaskRequest)` | `ExecutionSnapshot` |
| `GetAsync(Guid)` | Snapshot or `null` |
| `CancelAsync(Guid)` | `false` when not found |
| `SubmitApprovalAsync(Guid, ApprovalDecision)` | `ApprovalResult` |
| `StreamEventsAsync(Guid, long)` | Incremental `IAsyncEnumerable<ExecutionEvent>` |
| `GetArtifactAsync(Guid)` | Artifact or `null` |
| `GetModelGatewayHealthAsync()` | `ModelGatewayHealth` |
| `GetObservabilitySummaryAsync(...)` | `ObservabilitySummary` |
| `GetExecutionTimelineAsync(Guid)` | Timeline or `null` |

## Run the example and tests

```bash
dotnet run --project examples/sdks/dotnet
dotnet run --project tests/iRoute.UnitTests -- -reporter quiet
```

Public wire shapes are defined by the repository's
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
