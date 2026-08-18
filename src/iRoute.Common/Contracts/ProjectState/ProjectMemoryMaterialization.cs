namespace iRoute.Common;

public sealed record ProjectMemoryMaterialization(
    MemoryWriteResult Write,
    MemoryInvalidationResult InvalidatedMemory,
    ArtifactInvalidationResult InvalidatedArtifacts);
