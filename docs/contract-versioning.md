# Contract versioning

The public `v1` contract is the OpenAPI document, JSON Schemas, SSE event rules,
and error taxonomy under `spec/`. `iRoute.Common` is the matching .NET contract
assembly; it must not define a second wire shape.

## Compatible `v1` changes

- Add an optional request or response property with a safe default.
- Add an event type or error code that consumers may ignore or surface.
- Add an endpoint without changing existing endpoint behavior.
- Relax a validation rule without weakening tenant, permission, or safety boundaries.

## Breaking changes

- Remove or rename an endpoint, field, event, enum value, or error code.
- Make an optional field required or narrow a previously valid value range.
- Change the meaning, identity scope, ordering, or terminal semantics of a field.
- Reuse a persisted discriminator for a different concept.

Breaking wire changes require a new API major and a new compatibility snapshot.
Do not edit the `v1` snapshot to make an accidental break appear compatible.

Stored-state migrations follow expand-and-contract rules: new code expands the
schema additively, overlapping application versions can read it, and destructive
contraction happens only after the compatibility window. Package versions use
Semantic Versioning; prerelease versions may still introduce source-breaking
changes when release notes and migration guidance state them explicitly.
