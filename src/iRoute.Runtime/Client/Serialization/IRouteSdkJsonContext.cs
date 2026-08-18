using System.Text.Json;
using System.Text.Json.Serialization;
using iRoute.Common;

namespace iRoute.Runtime.Client;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskRequest))]
[JsonSerializable(typeof(ExecutionSnapshot))]
[JsonSerializable(typeof(ApprovalDecision))]
[JsonSerializable(typeof(ApprovalResult))]
[JsonSerializable(typeof(ArtifactSnapshot))]
[JsonSerializable(typeof(ModelGatewayHealth))]
[JsonSerializable(typeof(ObservabilitySummary))]
[JsonSerializable(typeof(ExecutionTimeline))]
[JsonSerializable(typeof(ExecutionEvent))]
internal sealed partial class IRouteSdkJsonContext : JsonSerializerContext
{
}
