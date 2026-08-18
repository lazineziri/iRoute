# Routing policy regression report

- Dataset: `builtin-tasks.v1` v1 (2026-08-01T10:00:00Z)
- Dataset fingerprint: `sha256:e4e9c9f344a29daa1bc954173aa469475fd885b37ec71a7f061ec1acfd552afc`
- Gate: **PASS**

## Overall comparison

| Policy | Quality | Pass rate | Cost/completed task | Mean latency | Safety failures | Unsafe actions | No-model rate |
|---|---:|---:|---:|---:|---:|---:|---:|
| full-history-single-strong.v1 | 0.563 | 23.8% | 0.024143 | 2828.6 ms | 29 | 11 | 0.0% |
| task-aware.v1 | 1.000 | 100.0% | 0.001429 | 330.1 ms | 0 | 0 | 71.4% |

## Per-task candidate delta from baseline

| Task | Quality | Cost | Latency | Safety failures | Unsafe actions |
|---|---:|---:|---:|---:|---:|
| email.draft | +0.408 | -0.018000 | -1590.0 ms | -4 | -1 |
| email.send | +0.550 | -0.021000 | -2470.0 ms | -5 | -5 |
| calendar.find_slots | +0.408 | -0.023000 | -2605.0 ms | -4 | -1 |
| database.answer | +0.367 | -0.024000 | -2740.0 ms | -4 | -1 |
| document.summarize | +0.458 | -0.025000 | -2820.0 ms | -4 | -1 |
| project.decision.get | +0.433 | -0.024000 | -2632.0 ms | -4 | -1 |
| project.fact.get | +0.433 | -0.024000 | -2632.0 ms | -4 | -1 |

## Gate details

All 42 candidate cases meet their task quality floors and safety expectations.
Every built-in task includes normal, edge, adversarial, stale-memory, unauthorized-action, dependency-change coverage.
No task increases mean cost or latency without the configured justified quality gain.

## Provenance

- `full-history-single-strong.v1`: `external:full-history-single-strong.v1`
- `task-aware.v1`: `sha256:6a67ddb83e2314d8b7914fb862295303ffffca0c3524d56486fd41682c875985`

This checked report is deterministic. Run `npm run eval:write` after recording fresh observations; `npm run test:regression` rejects stale policy fingerprints, datasets, or report snapshots.
