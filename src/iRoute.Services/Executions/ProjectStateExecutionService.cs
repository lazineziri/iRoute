using iRoute.Common;

namespace iRoute.Services;

public sealed partial class ExecutionService
{
    private static Problem CapabilityProblem(CapabilityInvocationException exception)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["capabilityFailureKind"] = exception.FailureKind.ToString()
        };
        if (!string.IsNullOrWhiteSpace(exception.Capability))
        {
            metadata["capability"] = exception.Capability;
        }

        if (!string.IsNullOrWhiteSpace(exception.ConnectorId))
        {
            metadata["connectorId"] = exception.ConnectorId;
        }

        return new Problem(
            exception.Code,
            "Capability invocation failed",
            exception.Message,
            exception.Retryable,
            metadata);
    }

    private async Task<IReadOnlyList<MemoryRecord>> MaterializeProjectMemoryAsync(
        ExecutionSnapshot snapshot,
        TaskRequest request,
        CancellationToken cancellationToken)
    {
        var results = await projectMemory.MaterializeAsync(
            request,
            snapshot.ExecutionId,
            cancellationToken);
        foreach (var result in results)
        {
            if (result.Write.Created)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemoryMaterialized,
                    new
                    {
                        result.Write.Record.MemoryId,
                        result.Write.Record.Kind,
                        result.Write.Record.Key,
                        result.Write.Record.Version,
                        result.Write.Record.ContentHash,
                        result.Write.Record.LifecycleStatus
                    },
                    cancellationToken);
            }

            if (result.Write.Previous is not null)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemorySuperseded,
                    new
                    {
                        memoryId = result.Write.Record.MemoryId,
                        supersedesMemoryId = result.Write.Previous.MemoryId,
                        result.Write.Record.Kind,
                        result.Write.Record.Key,
                        result.Write.Record.Version
                    },
                    cancellationToken);
            }

            if (result.InvalidatedMemory.MemoryIds.Count > 0)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.MemoryInvalidated,
                    new
                    {
                        sourceMemoryId = result.Write.Previous?.MemoryId,
                        memoryIds = result.InvalidatedMemory.MemoryIds,
                        reason = "dependency_superseded"
                    },
                    cancellationToken);
            }

            if (result.InvalidatedArtifacts.ArtifactIds.Count > 0)
            {
                await AppendEventAsync(
                    snapshot.ExecutionId,
                    ExecutionEventTypes.ArtifactInvalidated,
                    new
                    {
                        sourceMemoryId = result.Write.Previous?.MemoryId,
                        artifactIds = result.InvalidatedArtifacts.ArtifactIds,
                        reason = "dependency_superseded"
                    },
                    cancellationToken);
            }
        }

        return results.Select(result => result.Write.Record).ToArray();
    }

}
