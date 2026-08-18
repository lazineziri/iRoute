using System.Text.Json;
using iRoute.Common;

namespace iRoute.Data;

internal static class PersistenceMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ExecutionEntity ToEntity(
        ExecutionSnapshot snapshot,
        string? idempotencyKey,
        string? inputFingerprint) => new()
        {
            ExecutionId = snapshot.ExecutionId,
            TenantId = snapshot.TenantId,
            ActorId = snapshot.ActorId,
            ProjectId = snapshot.ProjectId,
            TaskType = snapshot.TaskType,
            TaskDefinitionVersion = snapshot.TaskDefinitionVersion,
            Status = snapshot.Status,
            CreatedAtUnixMilliseconds = snapshot.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds(),
            CancellationRequestedAtUnixMilliseconds = snapshot.CancellationRequestedAt?.ToUnixTimeMilliseconds(),
            IdempotencyKey = idempotencyKey,
            InputFingerprint = inputFingerprint,
            OutcomeJson = Serialize(snapshot.Outcome),
            ErrorJson = Serialize(snapshot.Error)
        };

    public static void Apply(ExecutionSnapshot snapshot, ExecutionEntity entity)
    {
        entity.TenantId = snapshot.TenantId;
        entity.ActorId = snapshot.ActorId;
        entity.ProjectId = snapshot.ProjectId;
        entity.TaskType = snapshot.TaskType;
        entity.TaskDefinitionVersion = snapshot.TaskDefinitionVersion;
        entity.Status = snapshot.Status;
        entity.UpdatedAtUnixMilliseconds = snapshot.UpdatedAt.ToUnixTimeMilliseconds();
        // CancellationRequestedAt is deliberately not written here: it is owned by
        // TryRequestCancellationAsync so a stale snapshot cannot erase a cancellation request.
        entity.OutcomeJson = Serialize(snapshot.Outcome);
        entity.ErrorJson = Serialize(snapshot.Error);
    }

    public static ExecutionSnapshot ToContract(ExecutionEntity entity) => new(
        entity.ExecutionId,
        entity.TaskType,
        entity.Status,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMilliseconds),
        Deserialize<TaskOutcome>(entity.OutcomeJson),
        Deserialize<Problem>(entity.ErrorJson),
        entity.TenantId,
        entity.ActorId,
        entity.ProjectId,
        entity.TaskDefinitionVersion,
        entity.CancellationRequestedAtUnixMilliseconds is { } cancellationRequestedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(cancellationRequestedAt)
            : null);

    public static ExecutionEventEntity ToEntity(ExecutionEvent executionEvent) => new()
    {
        ExecutionId = executionEvent.ExecutionId,
        Sequence = executionEvent.Sequence,
        EventType = executionEvent.Type,
        OccurredAtUnixMilliseconds = executionEvent.OccurredAt.ToUnixTimeMilliseconds(),
        DataJson = executionEvent.Data.GetRawText()
    };

    public static ExecutionEvent ToContract(ExecutionEventEntity entity) => new(
        entity.Sequence,
        entity.ExecutionId,
        entity.EventType,
        DateTimeOffset.FromUnixTimeMilliseconds(entity.OccurredAtUnixMilliseconds),
        JsonSerializer.Deserialize<JsonElement>(entity.DataJson, JsonOptions));

    public static ArtifactEntity ToEntity(ArtifactRecord artifact) => new()
    {
        ArtifactId = artifact.ArtifactId,
        TenantId = artifact.TenantId,
        ProjectId = artifact.ProjectId ?? string.Empty,
        TaskType = artifact.TaskType,
        TaskDefinitionVersion = artifact.TaskDefinitionVersion,
        ArtifactType = artifact.ArtifactType,
        Version = artifact.Version,
        InputHash = artifact.InputHash,
        ContentHash = artifact.ContentHash,
        ContentJson = artifact.Content.GetRawText(),
        EvidenceJson = JsonSerializer.Serialize(artifact.Evidence, JsonOptions),
        CreatedAtUnixMilliseconds = artifact.CreatedAt.ToUnixTimeMilliseconds(),
        ExpiresAtUnixMilliseconds = artifact.ExpiresAt?.ToUnixTimeMilliseconds(),
        IsActive = artifact.IsActive,
        LogicalKey = artifact.EffectiveLogicalKey,
        LifecycleStatus = artifact.LifecycleStatus,
        SupersedesArtifactId = artifact.SupersedesArtifactId,
        SupersededByArtifactId = artifact.SupersededByArtifactId,
        InvalidatedAtUnixMilliseconds = artifact.InvalidatedAt?.ToUnixTimeMilliseconds(),
        InvalidationReason = artifact.InvalidationReason
    };

    public static ArtifactRecord ToContract(
        ArtifactEntity entity,
        IReadOnlyList<DependencyReference>? dependencies = null) => new(
        entity.ArtifactId,
        entity.TenantId,
        string.IsNullOrEmpty(entity.ProjectId) ? null : entity.ProjectId,
        entity.TaskType,
        entity.TaskDefinitionVersion,
        entity.ArtifactType,
        entity.Version,
        entity.InputHash,
        entity.ContentHash,
        JsonSerializer.Deserialize<JsonElement>(entity.ContentJson, JsonOptions),
        JsonSerializer.Deserialize<EvidenceReference[]>(entity.EvidenceJson, JsonOptions) ?? [],
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMilliseconds),
        entity.ExpiresAtUnixMilliseconds is { } expiresAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : null,
        entity.IsActive,
        entity.LogicalKey,
        entity.LifecycleStatus,
        entity.SupersedesArtifactId,
        entity.SupersededByArtifactId,
        dependencies ?? [],
        entity.InvalidatedAtUnixMilliseconds is { } invalidatedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(invalidatedAt)
            : null,
        entity.InvalidationReason);

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string? value) where T : class =>
        value is null ? null : JsonSerializer.Deserialize<T>(value, JsonOptions);
}
