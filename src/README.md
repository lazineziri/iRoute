# Source layout

All projects in this repository are .NET. The API host also owns its static
dashboard assets. The folders here encode dependency direction. Inside a
project, code is grouped by product feature or adapter type rather than
accumulated in the project root:

```text
Core
├── iRoute.Contracts       public protocol records
└── iRoute.Core            domain policy and ports

Application
└── iRoute.Runtime         use cases and orchestration

Infrastructure
└── iRoute.Infrastructure  persistence and external adapters

Hosts
├── iRoute.Api             HTTP and SSE composition root
├── iRoute.Worker          durable background-process composition root
└── iRoute.Migrations      schema-management composition root

Clients
├── iRoute.Sdk.DotNet      public .NET client
└── iRoute.Cli             CLI built on the public client
```

Core has no dependency on outer layers. Application and Infrastructure may
depend on Core but not on each other. Hosts assemble the application and
infrastructure layers. The SDK depends only on Contracts, and the CLI delegates
to the SDK. See [`docs/architecture.md`](../docs/architecture.md) for the full
runtime topology and ownership rules.

## Organization rules

- Use `layer/project/feature` for production code. Examples are
  `Application/iRoute.Runtime/Routing` and
  `Infrastructure/iRoute.Infrastructure/Persistence/EntityFramework`.
- Keep `Program.cs` as a composition root. HTTP endpoints, identity, workers,
  and adapters live in named feature folders.
- Put interfaces and policy-owned records in Core. Put orchestration in
  Application and implementations of Core ports in Infrastructure.
- Separate adapter families. Entity Framework, in-memory storage, gateway
  resilience, capabilities, and observability do not share catch-all files.
- A file may contain several small types when they form one cohesive contract;
  unrelated services must be split by responsibility.
- Keep the established assembly namespaces and public package identities stable.
  Folder names communicate ownership without breaking consumers.
