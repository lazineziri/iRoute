<?php

declare(strict_types=1);

namespace IRoute;

final readonly class IRouteResponse
{
    public function __construct(public int $status, public string $body) {}
}
