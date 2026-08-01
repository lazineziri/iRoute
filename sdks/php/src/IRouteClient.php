<?php

declare(strict_types=1);

namespace IRoute;

final class IRouteClient
{
    /** @var \Closure(IRouteRequest): IRouteResponse */
    private readonly \Closure $transport;

    /** @param list<string> $permissionScopes */
    public function __construct(
        private readonly string $baseUrl,
        private readonly ?string $token = null,
        private readonly ?string $tenantId = null,
        private readonly ?string $actorId = null,
        private readonly array $permissionScopes = [],
        private readonly int $timeoutSeconds = 30,
        ?callable $transport = null,
    ) {
        $this->transport = $transport === null
            ? $this->defaultTransport(...)
            : \Closure::fromCallable($transport);
    }

    /** @param array<string, mixed>|string $requestOrTaskType @return array<string, mixed> */
    public function execute(array|string $requestOrTaskType, mixed $input = null): array
    {
        $request = is_array($requestOrTaskType)
            ? $requestOrTaskType
            : ['taskType' => $requestOrTaskType, 'input' => $input];
        $headers = ['Content-Type' => 'application/json'];
        if (isset($request['idempotencyKey']) && is_string($request['idempotencyKey'])) {
            $headers['Idempotency-Key'] = $request['idempotencyKey'];
        }
        return $this->requestJson('POST', '/v1/executions', $request, $headers);
    }

    /** @return array<string, mixed>|null */
    public function get(string $executionId): ?array
    {
        return $this->requestJson('GET', '/v1/executions/' . rawurlencode($executionId), notFound: true);
    }

    public function cancel(string $executionId): bool
    {
        $response = $this->send('POST', '/v1/executions/' . rawurlencode($executionId) . '/cancel');
        if ($response->status === 404) return false;
        $this->ensureSuccess($response);
        return true;
    }

    /** @param array<string, mixed> $decision @return array<string, mixed> */
    public function submitApproval(string $executionId, array $decision): array
    {
        return $this->requestJson(
            'POST',
            '/v1/executions/' . rawurlencode($executionId) . '/approvals',
            $decision,
            ['Content-Type' => 'application/json'],
        );
    }

    /** @return array<string, mixed>|null */
    public function getArtifact(string $artifactId): ?array
    {
        return $this->requestJson('GET', '/v1/artifacts/' . rawurlencode($artifactId), notFound: true);
    }

    /** @return array<string, mixed> */
    public function getModelGatewayHealth(): array
    {
        return $this->requestJson('GET', '/health/model-gateway', allowedStatuses: [503]);
    }

    /** @param array<string, string> $query @return array<string, mixed> */
    public function getObservabilitySummary(array $query = []): array
    {
        $parameters = http_build_query($query, '', '&', PHP_QUERY_RFC3986);
        return $this->requestJson(
            'GET',
            '/v1/observability/summary' . ($parameters === '' ? '' : '?' . $parameters),
        );
    }

    /** @return array<string, mixed>|null */
    public function getExecutionTimeline(string $executionId): ?array
    {
        return $this->requestJson(
            'GET',
            '/v1/observability/executions/' . rawurlencode($executionId),
            notFound: true,
        );
    }

    /** @return \Generator<int, array<string, mixed>> */
    public function streamEvents(string $executionId, int $afterSequence = 0): \Generator
    {
        $response = $this->send(
            'GET',
            '/v1/executions/' . rawurlencode($executionId) . '/events?after=' . $afterSequence,
            headers: ['Accept' => 'text/event-stream'],
        );
        $this->ensureSuccess($response);
        $data = [];
        foreach (preg_split('/\r?\n/', $response->body) ?: [] as $line) {
            if ($line === '') {
                if ($data !== []) {
                    yield json_decode(implode("\n", $data), true, flags: JSON_THROW_ON_ERROR);
                    $data = [];
                }
            } elseif (str_starts_with($line, 'data:')) {
                $data[] = ltrim(substr($line, 5));
            }
        }
        if ($data !== []) yield json_decode(implode("\n", $data), true, flags: JSON_THROW_ON_ERROR);
    }

    /**
     * @param array<string, mixed>|null $body
     * @param array<string, string> $headers
     * @param list<int> $allowedStatuses
     * @return array<string, mixed>|null
     */
    private function requestJson(
        string $method,
        string $path,
        ?array $body = null,
        array $headers = [],
        bool $notFound = false,
        array $allowedStatuses = [],
    ): ?array {
        $response = $this->send($method, $path, $body, $headers);
        if ($response->status === 404 && $notFound) return null;
        if (!in_array($response->status, $allowedStatuses, true)) $this->ensureSuccess($response);
        return json_decode($response->body, true, flags: JSON_THROW_ON_ERROR);
    }

    /** @param array<string, mixed>|null $body @param array<string, string> $headers */
    private function send(string $method, string $path, ?array $body = null, array $headers = []): IRouteResponse
    {
        if ($this->token !== null && $this->token !== '') {
            $headers['Authorization'] = 'Bearer ' . $this->token;
        }
        if ($this->tenantId !== null && $this->tenantId !== '') $headers['X-Tenant-Id'] = $this->tenantId;
        if ($this->actorId !== null && $this->actorId !== '') $headers['X-Actor-Id'] = $this->actorId;
        if ($this->permissionScopes !== []) $headers['X-Permission-Scopes'] = implode(' ', $this->permissionScopes);
        $encodedBody = $body === null
            ? null
            : json_encode($body, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES);
        return ($this->transport)(new IRouteRequest(
            $method,
            rtrim($this->baseUrl, '/') . $path,
            $headers,
            $encodedBody,
        ));
    }

    private function ensureSuccess(IRouteResponse $response): void
    {
        if ($response->status >= 200 && $response->status < 300) return;
        throw IRouteApiException::fromResponse($response);
    }

    private function defaultTransport(IRouteRequest $request): IRouteResponse
    {
        $handle = curl_init($request->url);
        if ($handle === false) throw new \RuntimeException('Could not initialize cURL.');
        $headers = [];
        foreach ($request->headers as $name => $value) $headers[] = $name . ': ' . $value;
        curl_setopt_array($handle, [
            CURLOPT_CUSTOMREQUEST => $request->method,
            CURLOPT_HTTPHEADER => $headers,
            CURLOPT_POSTFIELDS => $request->body,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => $this->timeoutSeconds,
        ]);
        $body = curl_exec($handle);
        if ($body === false) throw new \RuntimeException(curl_error($handle));
        return new IRouteResponse((int) curl_getinfo($handle, CURLINFO_RESPONSE_CODE), $body);
    }
}
