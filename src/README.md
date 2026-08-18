# Source layout

The repository has six top-level .NET projects and no layer wrapper folders:

```text
iRoute.Common    the single home for contracts, DTOs, interfaces, ports, and shared primitives
iRoute.Services  policies, routing, gateways, connectors, and execution implementations
iRoute.Data      DbContext, entities, stores, and migrations
iRoute.Core      small execution facade that delegates through Common contracts
iRoute.Runtime   API, workers, migration commands, client CLI, and DI composition
iRoute.Tests     architecture and behavior verification
```

Allowed production references are:

```text
Services -> Common
Core     -> Common
Data     -> Common
Runtime  -> Core, Data, Services, Common
```

`iRoute.Tests` may reference all five production projects. Reverse references
and cycles are forbidden and covered by an architecture test.

## Organization rules

- Group code by feature inside its owning project; do not add new layer wrappers.
- Put every cross-project contract, DTO, interface, port, enum, and shared option
  in Common. Services contains implementations, never a second contract model.
- Keep Core as a stable delegation boundary. Business and routing logic belongs
  in Services, while database behavior belongs in Data.
- Keep Runtime as the only composition root and executable. `iroute serve`,
  `worker`, `migrate`, and client commands are modes of that executable.
- Keep Entity Framework, in-memory storage, gateway resilience, capabilities,
  and observability in cohesive files and feature folders.
- Prefer platform features such as `TimeProvider`, validated options,
  System.Text.Json source generation, frozen collections, `BackgroundService`,
  `HttpClientFactory`, generated logging, health checks, and OpenTelemetry.
- Do not hide model attempts, costs, retries, or deadlines in generic transport
  middleware; those are explicit product policies.

See [`docs/architecture.md`](../docs/architecture.md) for detailed ownership and
runtime flow.
