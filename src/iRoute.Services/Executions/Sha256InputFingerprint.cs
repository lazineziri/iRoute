using System.Security.Cryptography;
using System.Text.Json;
using iRoute.Common;

namespace iRoute.Services;

public sealed class Sha256InputFingerprint : IInputFingerprint
{
    public string Create(TaskRequest request, int taskDefinitionVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("taskType", request.TaskType);
            writer.WriteNumber("taskDefinitionVersion", taskDefinitionVersion);
            writer.WriteNumber("fingerprintVersion", 2);
            writer.WritePropertyName("input");
            CanonicalJson.Write(writer, request.Input);
            writer.WritePropertyName("generationPolicy");
            CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(new
            {
                request.Constraints?.MaxInputTokens,
                request.Constraints?.MaxOutputTokens,
                request.Constraints?.MinimumQuality,
                RequireEvidence = request.Constraints?.RequireEvidence ?? false,
                AllowedRegions = request.Constraints?.AllowedRegions?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                request.Constraints?.RequiredResidency
            }));
            WriteMetadata(writer, request);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public string CreateForSubmission(TaskRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("taskType", request.TaskType);
            writer.WriteString("projectId", request.ProjectId);
            writer.WritePropertyName("input");
            CanonicalJson.Write(writer, request.Input);
            writer.WritePropertyName("constraints");
            CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(request.Constraints));
            WriteMetadata(writer, request);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteMetadata(Utf8JsonWriter writer, TaskRequest request)
    {
        writer.WritePropertyName("metadata");
        CanonicalJson.Write(writer, JsonSerializer.SerializeToElement(request.Metadata ?? EmptyMetadata));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();
}
