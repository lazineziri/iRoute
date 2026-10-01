# Product benchmark

The opt-in product benchmark compares a direct native model call with the full
iRoute execution pipeline. It is deliberately separate from the offline test
suite: normal development and CI never enable live inference automatically.
The [initial results](benchmarks/2026-10-01-initial.md) are exploratory synthetic
measurements, not a production savings or quality claim.

## Run it

Use a source checkout, .NET 10, and your own native iRoute ChatGPT sign-in as
described in [provider setup](model-providers.md). Plan access, model availability,
and quota remain provider-controlled. No API-key billing fallback is used.

```bash
dotnet test --solution iRoute.slnx --configuration Release
IROUTE_RUN_LIVE_BENCHMARK=1 dotnet test --solution iRoute.slnx \
  --configuration Release --no-build \
  --filter-method '*CompareDirectModelsWithTheCompleteIRouteExecutionPipeline'
```

Optionally select one model with `IROUTE_BENCHMARK_MODEL`. The explicit allowlist
is `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, and `gpt-5.5`; an unknown model
fails instead of silently running zero cases. Astra is excluded.

Each model makes 17 inference HTTP requests: nine direct baselines and eight
optimized calls. One additional optimized execution reuses a previously created
artifact without a provider call. Each request permits one model call and no
tools, uses low reasoning, and has a 30-second execution deadline. The HTTP
counter fails closed beyond 20 inference requests per model. Model discovery and
OAuth refresh are not inference calls and are not included in this counter.
Subscription output limits are post-completion acceptance checks, not provider
consumption ceilings. Run only with provider usage settings you accept.

The focused project-memory follow-up makes four model calls and four subsequent
zero-model lookups when all models are selected:

```bash
IROUTE_RUN_LIVE_BENCHMARK=1 dotnet test --solution iRoute.slnx \
  --configuration Release --no-build \
  --filter-method '*SupersededSourcesCannotPoisonSubsequentNoModelLookups'
```

## Comparison and measurements

Both paths use the same unmodified native adapter, account access, selected model,
structured-output schema, task instructions, and reasoning effort. The baseline
passes the complete original JSON input with no compiled context. The optimized
path uses routing, bounded context, SQLite persistence, validation, materialized
memory, and artifact reuse. Model identity is pinned per comparison; this does
not measure cheaper-model selection or quality escalation.

Seven cold cases cover short summaries, short drafts, long irrelevant history,
duplicate facts, superseded facts, Albanian input requesting English output, and
a relevant approval code in older history. A new request with a new idempotency
key exercises warm artifact reuse. A changed source must generate fresh work
and must not return the previous meeting time. Each case gets an isolated project
and each model a fresh temporary database. Direct-first and optimized-first order
alternate for the seven cold cases.

Results use provider-reported `inputTokens` and `outputTokens`, not the context
compiler's estimate. Input totals already include `cachedInputTokens`; output
totals already include `reasoningTokens`. Neither subset is added or subtracted
again. Null breakdowns mean unknown. Failed-call usage is unknown, never treated
as zero. Only a successful execution with zero observed inference calls can be
reported as no-model reuse. Subscription monetary cost remains unknown.

Whole-call elapsed time includes local orchestration on the optimized path.
Provider caching is not disabled and its reported count is retained. Output
length and reasoning can vary even with the same inputs; input savings are the
more direct measure of context reduction than input-plus-output savings.

`artifacts/benchmarks/run-<unique-id>/` retains `results.json`, `results.md`, and
append-only `progress.jsonl`. These are local, ignored build artifacts. Reports
contain synthetic outputs, observed usage, error codes, context decisions,
timings, and HTTP-call counts; never authorization headers or credentials.
Existing runs are not overwritten. Inspect every failed or interrupted run,
not only the final passing summary.

## Limits and interpretation

This is one observation per pair, not a confidence interval, representative
customer distribution, load test, or broad quality evaluation. Fact checks look
for required project/day/time/code strings and prohibit named stale facts. They
do not establish equivalence of every claim, calibrated confidence, writing
quality, resistance to adversarial input, or compliance with every requested
format/length constraint. Source examples remain small and repetitive.

The compiler's UTF-8-bytes/4 estimate omits provider framing and is not exact
tokenization. It remains suitable only for approximate local context selection.
Real provider usage can exceed that estimate. See OpenAI's
[token counting guide](https://developers.openai.com/api/docs/guides/token-counting)
and [cached-token diagnostics](https://developers.openai.com/api/docs/guides/prompt-caching/diagnostics).

Warm savings must be presented with the cost of creating the reusable result.
The nine-case workload includes the cold seed. Large reductions on deliberately
redundant inputs must not be advertised as universal savings. Broader repeated
customer-like tests, independent quality scoring, quota contention, and sustained
load are required before beta claims or automatic measured-profile promotion.

## Repeated correctness follow-up

The [hardening report](benchmarks/2026-10-01-hardening.md) adds two independent
cold repetitions of four scenarios: out-of-order active versions, unpaid invoice
status, an older Unicode approval fact, and rejection of an archived injection.
It uses field-specific deterministic rubrics with positive/negative controls,
not human semantic scoring. It does not compare direct baselines or estimate
additional token savings. Native Claude CLI HTTP request counts remain unknown.

```bash
IROUTE_RUN_LIVE_HARDENING=1 dotnet test --solution iRoute.slnx \
  --configuration Release --filter-method '*RepeatedColdOutputsPreserveCurrentFactsPaymentStatusAndInstructionBoundaries'
IROUTE_RUN_LIVE_CLAUDE=1 dotnet test --solution iRoute.slnx \
  --configuration Release --filter-method '*OfficialClaudeSubscriptionCompletesTheNativeIRoutePipelineWithoutTools'
```

Each test uses synthetic data, no tools, no API-billing fallback, isolated project
state, and at most eight inference invocations per selected model. The four-model
ChatGPT allowlist excludes Astra. Run only with subscription usage limits you accept.

## Upgrade note

The result fingerprint now includes output-affecting constraints and metadata.
Artifact fingerprints from the previous algorithm intentionally miss; new
executions regenerate rather than reusing a result under unverified policy.
Submission fingerprints also include constraints and metadata. Reusing an old
persisted idempotency key can therefore produce an idempotency conflict after
upgrading. Finish/poll old executions and use a new key for a new submission;
do not resubmit external writes under new keys without first reconciling the
previous execution. No database or artifact data is deleted by this change.

The lifecycle guard prevents new inactive/expired/superseded sources from being
materialized as active memory and preserves explicit source expiry. It does not
automatically repair already persisted stale records. Operators must review
affected project state and dependencies before relying on historical lookups.
