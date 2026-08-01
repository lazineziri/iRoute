<?php

declare(strict_types=1);

use IRoute\IRouteApiException;
use IRoute\IRouteClient;
use IRoute\IRouteRequest;
use IRoute\IRouteResponse;

require_once __DIR__ . '/../src/IRouteRequest.php';
require_once __DIR__ . '/../src/IRouteResponse.php';
require_once __DIR__ . '/../src/IRouteApiException.php';
require_once __DIR__ . '/../src/IRouteClient.php';

$fixture = [];
foreach (file(__DIR__ . '/../../conformance/v1.properties', FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) as $line) {
    [$key, $value] = explode('=', $line, 2);
    $fixture[$key] = $value;
}
$decode = static fn(string $key): string => base64_decode($fixture[$key], true);
$captured = null;
$response = new IRouteResponse((int) $fixture['response.status'], $decode('response.body.base64'));
$client = createClient($fixture, static function (IRouteRequest $request) use (&$captured, $response): IRouteResponse {
    $captured = $request;
    return $response;
});
$result = $client->execute(json_decode($decode('request.body.base64'), true, flags: JSON_THROW_ON_ERROR));
requireThat($captured instanceof IRouteRequest, 'request capture');
requireThat($captured->method === $fixture['request.method'], 'request method');
requireThat(parse_url($captured->url, PHP_URL_PATH) === $fixture['request.path'], 'request path');
requireThat($captured->headers['Authorization'] === $fixture['request.header.authorization'], 'authorization');
requireThat($captured->headers['Content-Type'] === $fixture['request.header.content-type'], 'content type');
requireThat($captured->headers['X-Tenant-Id'] === $fixture['request.header.tenant'], 'tenant');
requireThat($captured->headers['X-Actor-Id'] === $fixture['request.header.actor'], 'actor');
requireThat($captured->headers['X-Permission-Scopes'] === $fixture['request.header.permission-scopes'], 'scopes');
requireThat($captured->headers['Idempotency-Key'] === $fixture['request.header.idempotency-key'], 'idempotency');
requireThat(json_decode($captured->body ?? '', true) === json_decode($decode('request.body.base64'), true), 'request body');
requireThat($result['executionId'] === '019fbc00-0000-7000-8000-000000000014', 'response body');

$streamClient = createClient($fixture, static fn(IRouteRequest $request): IRouteResponse =>
    new IRouteResponse((int) $fixture['stream.status'], $decode('stream.body.base64')));
$events = iterator_to_array($streamClient->streamEvents('019fbc00-0000-7000-8000-000000000014'));
requireThat(count($events) === (int) $fixture['stream.expected.count'], 'stream count');
requireThat(array_column($events, 'type') === explode(',', $fixture['stream.expected.types']), 'stream types');

$errorClient = createClient($fixture, static fn(IRouteRequest $request): IRouteResponse =>
    new IRouteResponse((int) $fixture['error.status'], $decode('error.body.base64')));
try {
    $errorClient->execute(json_decode($decode('request.body.base64'), true, flags: JSON_THROW_ON_ERROR));
    throw new RuntimeException('Expected IRouteApiException.');
} catch (IRouteApiException $error) {
    requireThat($error->status === (int) $fixture['error.status'], 'error status');
    requireThat($error->errorCode === $fixture['error.expected.code'], 'error code');
    requireThat($error->title === $fixture['error.expected.title'], 'error title');
    requireThat($error->detail === $fixture['error.expected.detail'], 'error detail');
    requireThat($error->responseBody === $decode('error.body.base64'), 'error body');
}

echo "PASS PHP SDK conformance\n";

/** @param array<string, string> $fixture */
function createClient(array $fixture, callable $transport): IRouteClient
{
    return new IRouteClient(
        'http://sdk-fixture.test',
        'fixture-token',
        $fixture['request.header.tenant'],
        $fixture['request.header.actor'],
        explode(' ', $fixture['request.header.permission-scopes']),
        transport: $transport,
    );
}

function requireThat(bool $condition, string $message): void
{
    if (!$condition) throw new RuntimeException('Conformance failure: ' . $message);
}
