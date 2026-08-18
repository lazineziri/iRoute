using System.Collections.Frozen;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class BuiltInCapabilityDefinitionRegistry : ICapabilityDefinitionRegistry
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object"
    });

    private static readonly FrozenDictionary<string, CapabilityDefinition> Definitions =
        CreateDefinitions().ToFrozenDictionary(
            item => Key(item.Capability, item.Version),
            StringComparer.OrdinalIgnoreCase);

    public Task<CapabilityDefinition?> FindAsync(
        string capability,
        int version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Definitions.GetValueOrDefault(Key(capability, version)));
    }

    public Task<IReadOnlyList<CapabilityDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CapabilityDefinition>>(Definitions.Values
            .OrderBy(item => item.Capability, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .ToArray());
    }

    private static IEnumerable<CapabilityDefinition> CreateDefinitions()
    {
        yield return Create(
            "email.read", CapabilityKind.Tool, SideEffectClass.ReadOnly,
            ["email:read"], CapabilityTrustLevel.Registered, CapabilityDataSensitivity.Confidential,
            "Read email through a bounded projection that excludes message bodies and transport metadata.");
        yield return Create(
            "email.draft.compose", CapabilityKind.Tool, SideEffectClass.None,
            ["email:draft"], CapabilityTrustLevel.Registered, CapabilityDataSensitivity.Confidential,
            "Create an email draft without sending or mutating an external mailbox.");
        yield return Create(
            "email.send", CapabilityKind.Tool, SideEffectClass.IrreversibleWrite,
            ["email:send"], CapabilityTrustLevel.Registered, CapabilityDataSensitivity.Confidential,
            "Send an approved email with an idempotent delivery reference.", writes: true);
        yield return Create(
            "calendar.read", CapabilityKind.Tool, SideEffectClass.ReadOnly,
            ["calendar:read"], CapabilityTrustLevel.Registered, CapabilityDataSensitivity.Confidential,
            "Read calendar availability and return bounded candidate slots.");
        yield return Create(
            "database.read", CapabilityKind.Tool, SideEffectClass.ReadOnly,
            ["database:read"], CapabilityTrustLevel.Internal, CapabilityDataSensitivity.Restricted,
            "Execute an allow-listed, tenant-scoped read query with fixed row and time limits.");
        yield return Create(
            "openapi.invoke", CapabilityKind.Api, SideEffectClass.ReadOnly,
            ["openapi:invoke"], CapabilityTrustLevel.Registered, CapabilityDataSensitivity.Internal,
            "Invoke a registered OpenAPI operation with a fixed destination and response projection.");
        yield return Create(
            "mcp.invoke", CapabilityKind.Mcp, SideEffectClass.ReadOnly,
            ["mcp:invoke"], CapabilityTrustLevel.ExternalUntrusted, CapabilityDataSensitivity.Internal,
            "Invoke a registered MCP server and tool through an untrusted-output projection.");
        yield return Create(
            "agent.result.ingest", CapabilityKind.Agent, SideEffectClass.None,
            ["agent:ingest"], CapabilityTrustLevel.ExternalUntrusted, CapabilityDataSensitivity.Internal,
            "Validate and ingest a typed agent result with provenance, freshness, and dependencies.");
    }

    private static CapabilityDefinition Create(
        string capability,
        CapabilityKind kind,
        SideEffectClass sideEffectClass,
        IReadOnlyList<string> permissionScopes,
        CapabilityTrustLevel trustLevel,
        CapabilityDataSensitivity sensitivity,
        string description,
        bool writes = false) => new(
            capability,
            1,
            kind,
            description,
            ObjectSchema,
            ObjectSchema,
            sideEffectClass,
            [],
            permissionScopes,
            new CapabilityIdempotencyPolicy(writes, writes),
            new CapabilityRetryPolicy(1, []),
            new CapabilityServiceProfile(25, 0m, 0.99m, 0.99m),
            writes ? CapabilityCacheability.Never : CapabilityCacheability.Scoped,
            writes ? null : 300,
            sensitivity,
            trustLevel,
            trustLevel == CapabilityTrustLevel.ExternalUntrusted
                ? CapabilityIsolation.Remote
                : CapabilityIsolation.InProcess);

    private static string Key(string capability, int version) => $"{capability}@{version}";
}
