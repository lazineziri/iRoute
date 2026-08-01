# Public contract versioning

The adopter-facing support window, SDK, stored-state, configuration, and
deployment guarantees are defined in [compatibility.md](compatibility.md).

OpenAPI 3.1 and JSON Schema 2020-12 under `spec/` are the language-neutral public contract. The current public API major is `v1`; task definitions, capabilities, plans, and artifacts carry their own integer versions.

## Compatibility rules

- Removing or renaming an endpoint, field, event, status, error code, or enum member is breaking.
- Changing a field type, meaning, format, default, validation range, or optional field to required is breaking.
- A breaking transport change requires `/v2`, a new schema identifier path, and a migration guide. The v1 contract remains supported for the documented compatibility window.
- Adding an optional field is compatible. Additive fields remain optional until every official SDK passes the shared conformance suite.
- New event types, error codes, and enum values may be added in v1. Consumers must ignore unknown events and fields and surface unknown status/error values safely.
- A task or capability behavior change that would reinterpret stored state requires a new task or capability version even when the wire shape is unchanged.
- Schema identifiers are immutable. A correction that narrows accepted v1 input is breaking unless it closes a documented security vulnerability.
- Deprecation is documented before removal and does not silently change runtime behavior.

## Automated gate

`tools/contract-compatibility.test.mjs` compares the current OpenAPI and JSON Schema surface with `spec/compatibility/v1/public-contract.snapshot.json`. The gate protects v1 operations, required fields, existing properties, event types, status values, resolution levels, and error codes. An intentional breaking change must introduce a new major baseline instead of editing the v1 snapshot in place.
