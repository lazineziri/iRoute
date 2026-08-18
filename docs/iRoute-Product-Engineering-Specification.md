# iRoute Product and Engineering Specification

- **Version:** 1.0
- **Status:** Approved implementation baseline
- **Date:** 31 July 2026
- **Public identity:** iRoute
- **License:** Apache License 2.0
- **Core runtime:** .NET 10 LTS / ASP.NET Core
- **Primary objective:** Maximum validated task completion at the lowest justified token, cost, latency, and infrastructure budget.

> Decision: iRoute Core, the public protocol, and the official .NET SDK are free for everyone under Apache 2.0. Commercial revenue is built around managed cloud, enterprise operations, governance, support, SLAs, and professional services.

## 1. Executive definition

iRoute is an open-source, task-aware AI execution runtime. It resolves requests from trusted state and deterministic capabilities before considering generation, compiles only the context required for unresolved work, chooses a bounded execution path under quality, cost, latency, and safety constraints, validates the outcome, and materializes reusable project state.

iRoute is not another model adapter. Existing gateways already normalize access to hosted and self-hosted models. iRoute consumes a generic model-gateway contract and concentrates on the harder application-level problem: deciding whether a model is needed, what task must be performed, which context is justified, which capabilities may run, how results are validated, and what should be retained or invalidated afterward.

The product promise is efficiency with evidence. Cheap output that fails the task is waste. High-quality output produced through avoidable calls, repeated history, or unnecessary infrastructure is also waste. iRoute optimizes the completed and validated task, not price per token in isolation.

## 2. Problem and opportunity

AI-enabled products repeatedly rebuild the same fragile layer between an application and a model gateway:

- task classification and decomposition;
- conversation, project, and organizational context assembly;
- short-term and durable memory;
- deterministic lookup and cache reuse;
- capability execution across databases, APIs, email, calendars, MCP, and other agents;
- permissions, approvals, and idempotency;
- validation, retry, escalation, and human review;
- token, cost, latency, and quality measurement;
- cleanup, retention, supersession, and dependency invalidation.

Without this layer, applications send too much history to one large model, retry failures without learning, lose useful decisions inside chat logs, and create unsafe paths from probabilistic output to external side effects. New SaaS teams should not have to design this foundation independently.

## 3. Product goals

1. Avoid a model call whenever fresh trusted state or deterministic execution can satisfy the task.
2. When generation is necessary, choose the least expensive path expected to meet the task's explicit quality and safety floor.
3. Keep authoritative outputs as versioned artifacts, facts, decisions, evidence, and dependency edges rather than raw chat history.
4. Make context a compiled, inspectable execution input with a budget and manifest.
5. Separate planning from tool execution, permission enforcement, and side effects.
6. Make every result traceable to source versions, policies, capabilities, attempts, validation, and usage.
7. Run in one lightweight container for evaluation and scale horizontally without changing public contracts.
8. Offer an idiomatic .NET SDK while maintaining one execution engine and one language-neutral protocol.

## 4. Explicit non-goals

- Building a complete model-provider adapter ecosystem.
- Hosting foundation models inside the iRoute runtime container.
- Claiming universally perfect or always-best output.
- Creating a general autonomous-agent platform in the first release.
- Allowing models to issue arbitrary SQL or arbitrary HTTP calls.
- Reimplementing execution logic outside the .NET runtime.
- Automatically inserting complete conversation history into prompts.
- Requiring Redis, Kubernetes, a vector database, or an external queue for the developer profile.
- Sending prompts, responses, or telemetry to iRoute-controlled services by default.

## 5. Primary users and jobs

### 5.1 AI SaaS engineers

They need a stable runtime for task routing, memory, context, approvals, validation, and cost control so they can focus on their product domain.

### 5.2 Platform teams

They need consistent execution behavior across multiple applications and programming languages, with tenant isolation, auditability, deployment control, and central policy.

### 5.3 Product and operations teams

They need to understand whether requests were answered from memory, tools, or models; why a path was selected; what it cost; and whether quality improved or regressed.

### 5.4 Open-source contributors

They need explicit module ownership, language-neutral contracts, evaluation fixtures, architecture decisions, and contribution gates that prevent the core from becoming a provider-specific collection of integrations.

## 6. Supported task families

The first supported families prove different context, capability, evidence, and side-effect patterns.

| Task family | First capabilities | Side-effect class | Required validation |
|---|---|---|---|
| Email | search context, draft, revise, propose send | none or irreversible write | recipient, intent, claims, tone, approval before send |
| Calendar | read events, find slots, propose event | read-only or reversible write | timezone, participants, conflicts, approval before create |
| Database | read-only query and answer | read-only | allow-listed query shape, tenant scope, evidence rows |
| HTTP/OpenAPI | invoke registered operations | varies by operation | schema, authorization, idempotency, response projection |
| MCP | invoke registered server tools | varies by tool | capability policy, trust level, schema, output projection |
| Agent output | ingest typed result from another agent | none | provenance, freshness, schema, confidence, dependency check |
| Documents | summarize, extract, compare, transform | none | source coverage, citations, output schema |

The runtime supports task definitions, not hard-coded conversational personalities. New families are added through versioned definitions, handlers, policies, validators, and evaluation datasets.

## 7. Core operating model

### 7.1 Resolution cascade

Every request passes through the cheapest safe level capable of satisfying it.

| Level | Resolution path | Generative call |
|---|---|---|
| L0 | Exact idempotent outcome or fresh active artifact | none |
| L1 | Structured fact, active decision, or deterministic calculation | none |
| L2 | Scoped semantic memory with valid dependencies | none; embedding lookup allowed |
| L3 | Deterministic registered capability | none |
| L4 | Small task-specific model | one bounded call |
| L5 | Stronger model or bounded multi-step workflow | one or more justified calls |
| L6 | Independent verifier or human approval | risk-driven escalation |

Acceptance of a no-model result requires scope match, freshness, permission, dependency validity, and task-specific confidence. Semantic similarity alone is never proof that an old answer is still correct.

### 7.2 Direct and workflow paths

The direct path handles retrieval, deterministic calculations, simple transformations, and a single bounded generation. The workflow path is used only when a task requires multiple dependencies, capabilities, evidence sources, approvals, or independent validation.

Planning carries a tax. iRoute must skip planning when a static task definition or direct handler is sufficient. A planner call is justified only when its measured quality improvement exceeds its additional cost and latency.

### 7.3 Task decomposition

Plans are typed directed acyclic graphs with bounded depth, breadth, attempts, time, and cost. Every step declares:

- input and output schema;
- required capability;
- dependencies and join behavior;
- side-effect class;
- retry and timeout policy;
- validation rule;
- materialization policy;
- maximum token and cost budget.

Free-form model plans cannot execute directly. They must compile into a valid plan schema and pass policy validation.

## 8. Context engineering

### 8.1 Context is compiled, not accumulated

The context compiler starts from the task definition and creates a manifest of required facts, artifacts, evidence, preferences, constraints, and prior decisions. It retrieves candidates, validates scope and freshness, removes duplicates, projects only relevant fields, and fits the result within the task budget.

The compiler records what was included, excluded, truncated, summarized, or rejected and why. This makes context behavior observable and testable.

### 8.2 Context priority

1. System and safety policy.
2. Task definition and output contract.
3. Current explicit user request.
4. Active decisions and constraints.
5. Authoritative artifacts and connector evidence.
6. Stable user or project preferences.
7. Recent relevant events.
8. Summaries of older state.
9. Raw history only when required for evidence.

### 8.3 Context budgets

Budgets are set per task and include maximum input tokens, output tokens, deadline, cost, evidence requirements, and acceptable truncation behavior. Over-budget context is not silently sent to a larger model. The compiler first removes duplication, projects fields, selects smaller evidence slices, and uses an existing trusted summary. New summarization calls require a measurable benefit.

## 9. Memory and artifact lifecycle

### 9.1 State classes

| Class | Purpose | Typical retention |
|---|---|---|
| Request cache | Exact repeat and idempotency handling | short TTL |
| Working memory | Current execution scratch state | execution lifetime |
| Short-term memory | Recent scoped facts and events | configurable days |
| Active decisions | Current choices, constraints, and approvals | until superseded |
| Durable artifacts | Validated documents, outputs, and datasets | policy-controlled |
| Evidence references | Pointers and hashes for authoritative sources | follows artifact |
| Evaluation records | Quality, cost, latency, and safety results | long-term aggregate |

### 9.2 Materialization

Successful work is converted into typed outcomes. A generated email draft becomes an artifact with version, content hash, task definition, context manifest, evidence, and dependencies. Reusing this artifact does not require reconstructing the original conversation.

### 9.3 Cleanup

Cleanup runs asynchronously and must remain dependency-aware. It performs TTL expiry, quota enforcement, content-hash deduplication, supersession, cold archival, cache eviction, and deletion propagation. It never removes state required by an active execution or retained artifact.

### 9.4 Invalidation

An outcome is stale when an authoritative dependency changes, a newer decision supersedes it, its source disappears, its policy version becomes incompatible, or its retention policy expires. Invalidation follows recorded dependency edges; it does not erase unrelated project memory.

## 10. Routing intelligence

### 10.1 Objective

iRoute first satisfies mandatory safety and quality constraints. Among eligible execution paths, it minimizes expected total cost and latency:

`eligible = safety_pass AND expected_quality >= quality_floor`

`score = quality_weight * expected_quality - cost_weight * expected_cost - latency_weight * expected_latency - risk_weight * uncertainty`

Weights are task-specific and versioned. A low-cost option below the quality floor is ineligible, not preferred.

### 10.2 Capability registry

Every deterministic tool, model route, API, MCP tool, and agent is described by a normalized capability definition:

- capability and version;
- supported task families;
- input and output schemas;
- cost and latency estimates;
- context and output limits;
- safety and side-effect class;
- tenant and actor permissions;
- observed quality and reliability;
- health and availability;
- data residency and sensitivity constraints.

### 10.3 Initial routing policy

The public MVP uses deterministic rules plus measured capability profiles. Adaptive selection and bandit-style learning are P2 features and cannot ship until the evaluation harness, traffic safeguards, exploration limits, rollback, and per-task quality monitoring exist.

### 10.4 Failure behavior

Retries are bounded and allowed only for classified transient failures. The runtime may fall back to another eligible capability when doing so remains inside task budgets and policy. It does not loop between models without a new hypothesis or changed input.

## 11. Capability execution

### 11.1 Normalized contract

All capabilities expose typed input, typed output, policy metadata, cancellation, deadlines, usage, evidence, and failure classification. Transport-specific results are projected into a small result envelope before they enter model context.

### 11.2 Databases

The first database connector is read-only. It uses predefined query templates or a separately validated intermediate query representation. Tenant filters, row limits, timeouts, allowed schemas, and evidence rows are enforced outside the model.

### 11.3 HTTP and OpenAPI

Only registered operations may execute. Registration fixes host, method, path template, credential binding, schema, side-effect class, idempotency behavior, and response projection. Models never receive raw secrets or permission to choose arbitrary destinations.

### 11.4 MCP

MCP servers are treated as external capability providers, not automatically trusted peers. Each server and tool receives an identity, trust level, schema, permission policy, timeout, output limit, and side-effect classification.

### 11.5 Other agents

Agent output is untrusted until it passes schema, provenance, freshness, dependency, and policy validation. Agent chains use the same bounded execution graph as other capabilities.

## 12. Model-gateway boundary

iRoute defines one generic external contract. The request includes capability, task input, compiled context, maximum output tokens, deadline, and correlation identifiers. The response includes typed output, normalized usage, confidence, evidence, latency, and classified failure information.

The gateway is responsible for provider credentials, provider protocol differences, model aliases, transport streaming, and provider health. iRoute is responsible for task policy, path selection, context, validation, approvals, materialization, and learning from results.

No provider-specific model names or request formats belong in Core or Runtime. Gateway implementations may be delivered as optional Infrastructure modules without changing public execution contracts.

## 13. Validation and quality

Validation is task-specific and layered:

1. Structural validation against the output schema.
2. Deterministic domain validation such as dates, totals, recipients, and allowed values.
3. Evidence validation against source versions and dependency scope.
4. Policy validation for permissions, sensitive data, and side effects.
5. Optional independent model verification when its measured benefit justifies its cost.
6. Human approval for high-impact or externally visible actions.

Confidence is not accepted solely because a model supplied a number. iRoute derives confidence from validation outcomes, evidence coverage, capability history, agreement checks, and uncertainty signals.

## 14. Security, permissions, and approvals

### 14.1 Trust boundaries

The client, runtime, external capability, model gateway, state store, object store, and telemetry destination are separate trust boundaries. Every transition is authenticated, authorized, scoped, bounded, and audited.

### 14.2 Side-effect classes

| Class | Example | Default behavior |
|---|---|---|
| None | drafting or summarization | execute if task is authorized |
| Read-only | search email, calendar, or database | execute within scoped permission |
| Reversible write | create a draft or tentative event | require policy or explicit approval |
| Irreversible write | send email, publish, charge, delete | explicit approval and idempotency required |

### 14.3 Secrets and data

Secrets remain in secret stores or connector bindings and are never inserted into prompts, artifacts, logs, or events. Sensitive payload logging is off by default. Encryption in transit is mandatory; encryption at rest is required for durable profiles.

### 14.4 Prompt and tool attacks

Connector content is data, not instruction. Tool descriptions are trusted configuration. The runtime maintains instruction/data separation, allow lists, output schemas, payload size limits, URL restrictions, query restrictions, and approval gates.

## 15. Logical data model

| Entity | Purpose |
|---|---|
| TaskDefinition | Versioned task behavior, schemas, policies, budgets, and validators |
| CapabilityDefinition | Normalized model, tool, API, MCP, database, or agent capability |
| Execution | One request, actor, scope, state, budgets, and selected policy |
| ExecutionStep | One bounded operation, dependencies, attempts, usage, and outcome |
| ExecutionEvent | Ordered immutable lifecycle event |
| Approval | Actor decision authorizing a proposed side effect |
| TaskOutcome | Validated result and its reuse metadata |
| Artifact | Versioned reusable output or source with content hash |
| MemoryRecord | Scoped fact, preference, event, or decision with lifecycle state |
| DependencyEdge | Relationship used for freshness and targeted invalidation |
| EvidenceReference | Pointer to an authoritative source supporting a claim |
| RoutingPolicy | Versioned constraints, weights, and eligible capability rules |
| EvaluationResult | Quality, safety, cost, latency, and regression result |

Tenant, organization, project, user, and execution scope must be present in durable records. Storage implementations enforce tenant scope rather than relying only on API filters.

## 16. System architecture

### 16.1 Runtime stages

| Stage | Component | Responsibility |
|---|---|---|
| 1 | Client SDK | Send typed request and receive events/result; no routing logic |
| 2 | API and identity | Authenticate, validate, quota, and create execution |
| 3 | No-model resolver | Check cache, artifacts, decisions, memory, and deterministic paths |
| 4 | Context compiler | Select, project, deduplicate, and budget required context |
| 5 | Task planner | Select direct path or compile a bounded task graph |
| 6 | Policy engine | Enforce permissions, side effects, budgets, recursion, and trust |
| 7 | Scheduler | Run dependencies with cancellation, backpressure, and checkpoints |
| 8 | Capability executors | Invoke deterministic code, gateway, API, MCP, database, or agent |
| 9 | Validation engine | Validate schema, domain rules, evidence, policy, and quality |
| 10 | Materialization | Store outcomes, artifacts, memory, evidence, and dependencies |
| 11 | Lifecycle workers | Cleanup, archival, invalidation, deletion, and aggregate telemetry |

### 16.2 Codebase dependency direction

`Contracts <- Core <- Runtime <- Infrastructure <- API/Worker`

Contracts contains stable wire types only. Core contains domain rules and ports. Runtime contains use cases and orchestration. Infrastructure implements storage, network, and telemetry ports. API and Worker are composition roots. The .NET SDK depends on public contracts, not internal runtime assemblies.

### 16.3 Modular monolith first

The initial runtime is a modular monolith. Splitting services early would add network calls, deployment overhead, distributed transactions, and operational cost before workload evidence exists. Durable workers may scale separately while keeping the same modules and contracts.

## 17. Public API and compatibility

The protocol uses HTTPS REST and Server-Sent Events. OpenAPI 3.1 and JSON Schema 2020-12 are the language-neutral sources of truth.

Initial resources:

- `POST /v1/executions`
- `GET /v1/executions/{executionId}`
- `GET /v1/executions/{executionId}/events`
- `POST /v1/executions/{executionId}/cancel`
- `POST /v1/executions/{executionId}/approvals`
- `GET /v1/artifacts/{artifactId}`
- `GET /health/live`
- `GET /health/ready`

Breaking public changes require a new major API version. Additive fields remain optional until every official SDK passes conformance tests. Event consumers must ignore unknown event types and fields.

## 18. Deployment profiles

| Profile | Composition |
|---|---|
| Developer | One API container, in-memory or SQLite state, in-process queue, local artifacts, external model gateway |
| Team | API plus worker, PostgreSQL, optional S3-compatible object storage, multiple applications sharing one runtime |
| Production | Multiple stateless API nodes, durable workers, PostgreSQL, object storage, central secrets, OpenTelemetry collector |
| Sidecar | Runtime adjacent to one application for isolation or low latency, using the same public contracts |

Kubernetes is a documented production option, not a requirement. The first public quick start must work with one command and no commercial dependency.

## 19. Performance and resource requirements

Performance gates apply to the runtime excluding external capability latency.

| Measure | Developer target | Production target |
|---|---|---|
| Container idle memory | below 180 MB | below 250 MB per API replica |
| API overhead p95 | below 25 ms | below 15 ms inside region |
| Exact artifact resolution p95 | below 50 ms | below 30 ms |
| Added allocations | measured per request | regression gate on hot paths |
| Default concurrency | bounded | configured by CPU, memory, and dependency budgets |
| Startup readiness | below 5 seconds | below 10 seconds with durable dependencies |

Queues are bounded. Cancellation propagates to database, HTTP, MCP, agent, and gateway calls. Payloads stream where possible. Large artifacts are referenced rather than copied into events or prompts. The runtime does not load full conversation or artifact collections into memory.

## 20. Observability

Every request produces an execution trace containing:

- actor, tenant, project, and permission scope;
- normalized request and task definition version;
- resolution candidates and acceptance or rejection reasons;
- context manifest and token estimate;
- selected path and routing policy version;
- capabilities, attempts, latency, normalized usage, and failures;
- validation, escalation, and approval decisions;
- artifacts, facts, decisions, evidence, and dependency edges;
- cleanup, invalidation, and retention work scheduled.

Metrics include completed tasks, quality, no-model resolution rate, model calls avoided, tokens, estimated cost, latency, validation failures, approval rates, cache effectiveness, queue depth, retries, and cancellation. Prompt and output bodies are not exported by default.

## 21. Evaluation and regression

Evaluation precedes adaptive routing. Each supported task has normal, edge, adversarial, stale-memory, unauthorized-action, and dependency-change fixtures.

The baseline comparison is a full-history single-model implementation. iRoute must report:

- task success and task-specific quality;
- evidence precision and coverage;
- hallucination or unsupported-claim rate;
- model calls and tokens per completed task;
- execution cost and latency;
- no-model resolution correctness;
- external side-effect safety;
- resource use under concurrency.

A routing policy cannot merge if it reduces quality below the floor, increases unsafe actions, or increases cost or latency without a justified quality gain.

## 22. SDK strategy

One .NET engine serves the public HTTP and SSE protocol. The supported .NET SDK
provides authentication, typed requests and results, SSE consumption,
cancellation, idempotency-safe request handling, and idiomatic errors. It
contains no task planning or routing behavior.

Production baselines as of 31 July 2026:

| SDK | Baseline |
|---|---|
| .NET | .NET 10 LTS / C# 14 |

SDK releases use semantic versioning and remain aligned with the public protocol.

## 23. Open-source and commercial model

### 23.1 Free core

iRoute Core, self-hosting, public contracts, the official .NET SDK, basic storage profiles, evaluation framework, and required operational telemetry remain Apache 2.0 and usable commercially without permission.

### 23.2 Commercial products

Commercial value may include:

- hosted iRoute Cloud and managed upgrades;
- fleet management and organization-wide control plane;
- advanced identity federation, policy administration, and audit export;
- private networking, regional deployment, high availability, and disaster recovery;
- compliance evidence packs and controlled-release support;
- guaranteed response times, SLAs, training, and implementation services.

The core cannot be intentionally crippled to force cloud adoption. Paid features should address multi-team operations, governance, guarantees, and reduced operating burden.

### 23.3 Project protection

Apache 2.0 permits commercial forks. iRoute protects its position through trademark policy, release quality, compatibility, community, documentation, evaluation datasets, and a strong managed service rather than a restrictive runtime license.

## 24. Compliance posture

The project ships secure and privacy-conscious controls but makes no certification claim at launch. The baseline includes data minimization, configurable retention, deletion propagation, encryption, secret isolation, audit events, tenant scope, opt-in outbound telemetry, and deployment-region control.

SOC 2, ISO 27001, HIPAA, PCI DSS, sector-specific rules, and regulated data commitments are future commercial decisions requiring scope, legal review, operating processes, and evidence. They are not implied by technical features alone.

## 25. Engineering work breakdown

### P0: credible vertical slice

1. Freeze product boundaries and ADRs.
2. Complete OpenAPI, JSON Schemas, event contract, and error taxonomy.
3. Implement deterministic state machine, bounded scheduler, cancellation, and checkpoint model.
4. Implement permission, side-effect, approval, and idempotency policies.
5. Implement artifact, decision, fact, evidence, and dependency stores.
6. Implement no-model resolution and context compilation.
7. Implement the generic model-gateway contract.
8. Build evaluation fixtures before adaptive routing.
9. Deliver an investor-email vertical slice and compare it with the single-model baseline.

### P1: public MVP

1. Email, calendar, read-only database, OpenAPI, MCP, and agent-result capabilities.
2. Lifecycle cleanup, invalidation, deletion propagation, and archival.
3. OpenTelemetry, execution diagnostics, and cost/quality views.
4. The official .NET SDK and CLI.
5. SQLite and PostgreSQL profiles, container images, migrations, and upgrade procedures.
6. Security review, threat model, contribution process, and signed releases.

### P2: evidence-driven expansion

1. Adaptive routing with controlled exploration and rollback.
2. Advanced semantic memory where measured value justifies it.
3. Managed cloud control plane and enterprise operations.
4. Additional task packs and community capability modules.
5. Optional gRPC transport if benchmarks show a material advantage.

Work remains milestone-based until a team size and available engineering hours are committed. Inventing calendar dates without capacity would create false delivery confidence.

## 26. Release acceptance gates

The first public MVP is acceptable only when:

- the quick start runs locally without a paid dependency;
- all public examples validate against OpenAPI and JSON Schema;
- the official .NET SDK remains aligned with the public protocol;
- exact and deterministic paths demonstrably avoid generation;
- context manifests and usage are inspectable;
- external writes cannot execute without required authorization and idempotency;
- cancellation and restart do not duplicate completed work;
- tenant isolation has automated proof;
- evaluation shows quality at or above the baseline with lower model use on target tasks;
- telemetry exports no payload by default;
- upgrade, backup, restore, rollback, security reporting, and license files are complete;
- container and dependency scans have no unresolved critical vulnerabilities.

## 27. Principal risks and controls

| Risk | Control |
|---|---|
| Orchestration costs more than it saves | direct path, planning tax metric, per-task evaluation |
| Stale memory returns a confident wrong answer | dependency graph, freshness rules, source versions, invalidation |
| Router becomes a provider-adapter project | generic gateway boundary and dependency-direction review |
| Model output triggers unsafe action | typed plans, policy engine, approvals, idempotency |
| Too many modules slow early delivery | modular monolith and P0 vertical slice |
| SDK behavior drifts | one OpenAPI/schema source and conformance suite |
| Open-source adoption is weak | permissive license, lightweight quick start, useful defaults |
| Large vendors use the core without paying | accept distribution benefit; monetize operations and guarantees |
| Telemetry leaks customer data | local default, redaction, explicit opt-in exporter |
| Adaptive routing degrades quality | offline gates, shadow mode, exploration limits, rollback |

## 28. Fixed decisions and future gates

### Fixed

- Brand: iRoute.
- Core runtime: .NET 10 LTS.
- Architecture: modular monolith with separately scalable workers.
- Public transport: REST and SSE.
- State: versioned artifacts, facts, decisions, evidence, and dependencies.
- Execution: memory-first and deterministic-first.
- Provider integration: generic external gateway.
- License: Apache 2.0 for core and the .NET SDK.
- Commercialization: cloud, enterprise operations, support, and services.
- Delivery planning: milestones until capacity is known.
- Compliance: control baseline without certification claims.

### Evidence gates

- vector database adoption;
- Redis or external queue adoption;
- gRPC;
- service decomposition;
- adaptive model routing;
- default independent verifier;
- enterprise certification programs.

None of these is adopted because it is fashionable. Each requires benchmark, workload, security, or customer evidence.

## 29. Version baseline

The repository pins stable versions available on 31 July 2026 and excludes previews.

| Component | Selected stable version |
|---|---|
| .NET SDK | 10.0.100 feature band; verified with 10.0.102 |
| .NET / ASP.NET runtime | .NET 10 LTS; verified with 10.0.2 |
| EF Core | 10.0.10 |
| Npgsql EF provider | 10.0.3 |
| PostgreSQL | 18.4 |
| OpenTelemetry .NET | 1.17.0 |

Patch versions are updated through automated dependency pull requests and CI. Major upgrades require compatibility evidence and an ADR.

## 30. Immediate implementation sequence

1. Make the architecture scaffold build in CI and finalize the complete Apache 2.0 and Contributor Covenant texts.
2. Finalize task, capability, execution, event, outcome, artifact, evidence, approval, and error schemas.
3. Implement durable EF Core storage for SQLite and PostgreSQL with tenant-scope tests.
4. Implement the execution event stream, cancellation, checkpoint, and idempotency behavior.
5. Implement artifact reuse, decision lookup, and dependency invalidation.
6. Implement a bounded context compiler with a manifest and token estimator.
7. Implement the generic gateway and a gateway conformance test server.
8. Build the `email.draft` vertical slice using prior project state, deterministic lookups, generation only for unresolved content, validation, and artifact materialization.
9. Add the single-model baseline and evaluation report.
10. Publish the developer container and .NET preview SDK only after the release gates pass.

## Appendix A. Task definition template

Every task definition must specify identity and version, intent examples and exclusions, input and output schemas, required and optional context, eligible capabilities, budgets, minimum quality, evidence policy, side-effect class, approval policy, validation rules, retry and fallback rules, materialization behavior, retention, invalidation dependencies, and evaluation fixtures.

## Appendix B. Required execution trace

- authenticated actor, tenant, project, and permission scope;
- normalized request and resolved task definition version;
- memory and artifact candidates considered;
- reason a cached or remembered result was accepted or rejected;
- compiled-context manifest and token estimate;
- direct or workflow path and routing policy version;
- capabilities invoked, attempts, latency, and normalized usage;
- validation, escalation, and approval results;
- materialized artifacts, facts, decisions, and dependency edges;
- cleanup, invalidation, and retention actions scheduled.

## Appendix C. Definition of done for one task family

A task family is done only when its contracts, handlers, permissions, validation, evaluation fixtures, observability, documentation, SDK example, failure behavior, cleanup policy, and backward-compatibility tests are complete. A successful demo alone is not completion.

## Appendix D. Official version and licensing references

- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- .NET 10.0.10 release notes: https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.10/10.0.10.md
- NuGet Gallery: https://www.nuget.org/
- PostgreSQL release news: https://www.postgresql.org/about/news/
- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0
- Open Source Definition: https://opensource.org/osd
