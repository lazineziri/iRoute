<?php

declare(strict_types=1);

namespace IRoute;

final readonly class IRouteRequest
{
    /** @param array<string, string> $headers */
    public function __construct(
        public string $method,
        public string $url,
        public array $headers,
        public ?string $body,
    ) {}
}
