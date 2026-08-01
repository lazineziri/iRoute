<?php

declare(strict_types=1);

namespace IRoute;

final class IRouteApiException extends \RuntimeException
{
    public function __construct(
        public readonly int $status,
        public readonly ?string $errorCode,
        public readonly ?string $title,
        public readonly ?string $detail,
        public readonly string $responseBody,
    ) {
        parent::__construct($detail ?? $title ?? "iRoute request failed with HTTP {$status}");
    }

    public static function fromResponse(IRouteResponse $response): self
    {
        try {
            $problem = json_decode($response->body, true, flags: JSON_THROW_ON_ERROR);
        } catch (\JsonException) {
            $problem = [];
        }
        return new self(
            $response->status,
            isset($problem['code']) && is_string($problem['code']) ? $problem['code'] : null,
            isset($problem['title']) && is_string($problem['title']) ? $problem['title'] : null,
            isset($problem['detail']) && is_string($problem['detail']) ? $problem['detail'] : null,
            $response->body,
        );
    }
}
