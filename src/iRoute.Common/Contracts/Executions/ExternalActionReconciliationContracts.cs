namespace iRoute.Common;

/// <summary>
/// An external action left unresolved by a worker that stopped mid call.
/// </summary>
public sealed record UnresolvedExternalAction(
    string ActionId,
    string Capability,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// What the operator established actually happened outside iRoute.
/// </summary>
public sealed record ExternalActionReconciliation(
    string Outcome,
    string? Detail = null);
