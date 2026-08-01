<?php

declare(strict_types=1);

use IRoute\IRouteClient;

require_once __DIR__ . '/../../../sdks/php/vendor/autoload.php';

$client = new IRouteClient(
    getenv('IROUTE_URL') ?: 'http://localhost:8080',
    getenv('IROUTE_TOKEN') ?: null,
    getenv('IROUTE_TENANT') ?: 'demo',
    getenv('IROUTE_ACTOR') ?: 'sdk-example',
);
$result = $client->execute([
    'taskType' => 'email.draft',
    'input' => ['purpose' => 'Confirm the SDK quick start.'],
    'idempotencyKey' => 'php-example-' . bin2hex(random_bytes(8)),
]);
echo json_encode($result, JSON_PRETTY_PRINT | JSON_THROW_ON_ERROR) . PHP_EOL;
