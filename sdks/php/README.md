# iRoute PHP SDK

`iroute/sdk` is the PHP client for the iRoute v1 HTTP and SSE API. It uses cURL,
returns associative arrays, and supports execution, polling, cancellation,
approvals, artifacts, gateway health, observability, and buffered event replay.

Routing, provider selection, validation, workflow retries, and fallback remain
server-side.

## Status and requirements

- SDK version: `0.1.0-alpha.1`
- Minimum PHP: `8.3`
- Verified baseline: PHP `8.5`
- Required extension: `ext-curl`
- Composer package: `iroute/sdk`

Once published, the public alpha is installed from Packagist through the
official PHP mirror. The source installation below remains available.

## Start iRoute

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default API URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from Packagist

```bash
composer require iroute/sdk:0.1.0-alpha.1
```

## Install from source

Add the SDK checkout as a Composer path repository in the application's
`composer.json`:

```json
{
  "repositories": [
    {"type": "path", "url": "../iRoute/sdks/php", "options": {"symlink": true}}
  ],
  "require": {
    "iroute/sdk": "dev-main"
  }
}
```

Then run:

```bash
composer update iroute/sdk
```

For the checked repository example, install the SDK's own autoloader:

```bash
composer install --working-dir=sdks/php
```

## Create a client

```php
<?php

declare(strict_types=1);

use IRoute\IRouteClient;

require_once __DIR__ . '/vendor/autoload.php';

$client = new IRouteClient(
    baseUrl: getenv('IROUTE_URL') ?: 'http://localhost:8080',
    token: getenv('IROUTE_TOKEN') ?: null,
    tenantId: getenv('IROUTE_TENANT') ?: 'demo',
    actorId: getenv('IROUTE_ACTOR') ?: 'php-app',
    permissionScopes: [],
    timeoutSeconds: 30,
);
```

In Development the identity options become headers. In JWT mode the server
derives identity and scopes from token claims and will not allow headers to
elevate them.

## Submit an execution

```php
$idempotencyKey = 'php-email-' . bin2hex(random_bytes(8));

$request = [
    'taskType' => 'email.draft',
    'input' => ['purpose' => 'Confirm the PHP SDK integration.'],
    'idempotencyKey' => $idempotencyKey,
    'constraints' => [
        'maxOutputTokens' => 500,
        'deadlineMilliseconds' => 30_000,
        'minimumQuality' => 0.8,
    ],
];

$execution = $client->execute($request);

echo $execution['executionId'] . ' ' . $execution['status'] . PHP_EOL;
```

The shorthand form is available when no additional request fields are needed:

```php
$execution = $client->execute(
    'email.draft',
    ['purpose' => 'Confirm the PHP SDK integration.'],
);
```

Prefer the array form so the request includes a stable idempotency key.

## Poll until terminal

```php
$terminal = ['Succeeded', 'Failed', 'Cancelled', 'TimedOut'];

while (!in_array($execution['status'], $terminal, true)) {
    usleep(500_000);
    $current = $client->get($execution['executionId']);
    if ($current === null) {
        throw new RuntimeException('Execution is not visible to this tenant.');
    }
    $execution = $current;
}

if ($execution['status'] === 'Succeeded') {
    echo json_encode($execution['outcome']['output'], JSON_PRETTY_PRINT) . PHP_EOL;
} else {
    fwrite(STDERR, ($execution['error']['code'] ?? 'unknown') . ': ' .
        ($execution['error']['detail'] ?? 'execution failed') . PHP_EOL);
}
```

`WaitingForApproval` is non-terminal.

## Read event replay

```php
$lastSequence = 0;

foreach ($client->streamEvents($execution['executionId'], $lastSequence) as $event) {
    $lastSequence = (int) $event['sequence'];
    echo $lastSequence . ' ' . $event['type'] . PHP_EOL;
}
```

The default cURL transport returns the response body after the server closes it,
so the generator parses a buffered terminal replay rather than yielding live
network chunks. Applications requiring long-lived incremental events should
inject a streaming transport or consume the SSE endpoint through their HTTP
stack. Persist the sequence for reconnects and ignore unknown fields.

## Cancel an execution

```php
$accepted = $client->cancel($execution['executionId']);
if (!$accepted) echo "Execution was not found.\n";
```

Poll or replay events to confirm the durable terminal result.

## Approve or deny an action

Read `actionId` from an `approval.required` event:

```php
$result = $client->submitApproval($execution['executionId'], [
    'actionId' => $actionId,
    'approved' => true,
    'reason' => 'Reviewed by an authorized operator',
]);

$execution = $result['execution'];
```

The deciding token needs `approval:grant` and the action's required scope.

## Retrieve an artifact

```php
$artifacts = $execution['outcome']['artifacts'] ?? [];
if ($artifacts !== []) {
    $artifact = $client->getArtifact($artifacts[0]['artifactId']);
    echo json_encode($artifact['content'] ?? null, JSON_PRETTY_PRINT) . PHP_EOL;
}
```

Not found returns `null`, including for cross-tenant access.

## Health and observability

```php
$health = $client->getModelGatewayHealth();
echo $health['gatewayId'] . ' ' . $health['status'] . PHP_EOL;

$summary = $client->getObservabilitySummary([
    'from' => gmdate(DATE_ATOM, time() - 86400),
    'to' => gmdate(DATE_ATOM),
    'taskType' => 'email.draft',
]);
echo $summary['totals']['executions'] . PHP_EOL;

$timeline = $client->getExecutionTimeline($execution['executionId']);
echo ($timeline['traceId'] ?? 'not found') . PHP_EOL;
```

Gateway health returns a decoded body for HTTP `200` and `503`. Summary query
keys are `from`, `to`, `taskType`, and `policyVersion`. Observability is tenant
scoped, and reported cost has no currency in v1.

## Errors and timeouts

```php
use IRoute\IRouteApiException;

try {
    $execution = $client->execute($request);
} catch (IRouteApiException $error) {
    fwrite(STDERR, "HTTP {$error->status} {$error->errorCode}: {$error->detail}\n");
    fwrite(STDERR, $error->responseBody . PHP_EOL);
}
```

cURL failures throw `RuntimeException`. Configure the transport deadline with
`timeoutSeconds`. The SDK does not retry automatically; retrying a submission
requires the same idempotency key.

## Custom transport

The constructor's optional transport is a callable accepting `IRouteRequest`
and returning `IRouteResponse`. Use it for tests, proxies, streaming, metrics, or
application-specific TLS settings. SDK headers and problem-detail mapping remain
unchanged.

## Method reference

| Method | Result |
|---|---|
| `execute(array|string, input?)` | Execution array |
| `get(executionId)` | Array or `null` |
| `cancel(executionId)` | `false` when not found |
| `submitApproval(executionId, decision)` | Approval result array |
| `streamEvents(executionId, afterSequence=0)` | Buffered event generator |
| `getArtifact(artifactId)` | Array or `null` |
| `getModelGatewayHealth()` | Health array |
| `getObservabilitySummary(query=[])` | Summary array |
| `getExecutionTimeline(executionId)` | Timeline or `null` |

## Run the example and tests

From the repository root:

```bash
composer install --working-dir=sdks/php
php examples/sdks/php/execute.php
php sdks/php/tests/conformance.php
```

Public wire shapes are defined by the
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
