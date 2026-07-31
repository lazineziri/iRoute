<?php

declare(strict_types=1);

namespace IRoute;

final readonly class IRouteClient
{
    public function __construct(private string $baseUrl, private ?string $token = null) {}

    /** @return array<string, mixed> */
    public function execute(string $taskType, mixed $input): array
    {
        $handle = curl_init(rtrim($this->baseUrl, '/') . '/v1/executions');
        $headers = ['Content-Type: application/json'];
        if ($this->token !== null) $headers[] = 'Authorization: Bearer ' . $this->token;
        curl_setopt_array($handle, [
            CURLOPT_POST => true,
            CURLOPT_HTTPHEADER => $headers,
            CURLOPT_POSTFIELDS => json_encode(['taskType' => $taskType, 'input' => $input], JSON_THROW_ON_ERROR),
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 30,
        ]);
        $body = curl_exec($handle);
        if ($body === false) throw new \RuntimeException(curl_error($handle));
        $status = curl_getinfo($handle, CURLINFO_RESPONSE_CODE);
        if ($status < 200 || $status >= 300) throw new \RuntimeException("iRoute request failed with HTTP {$status}");
        return json_decode($body, true, flags: JSON_THROW_ON_ERROR);
    }
}
