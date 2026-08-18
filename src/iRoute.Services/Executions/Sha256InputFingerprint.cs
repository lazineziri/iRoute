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
            writer.WritePropertyName("input");
            CanonicalJson.Write(writer, request.Input);
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
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
