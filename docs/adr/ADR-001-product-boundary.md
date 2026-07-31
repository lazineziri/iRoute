# ADR-001: Product boundary

Status: Accepted

iRoute owns task resolution, context, memory, policy, capability orchestration, validation, materialization, and evaluation. It does not own provider-specific model adaptation or model hosting.

This boundary prevents duplicated adapter maintenance and keeps optimization focused on completed tasks.
