using System.Text.Json;
using System.Text.Json.Serialization;
using iRoute.Common;

namespace iRoute.Services;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ModelGatewayRequest))]
[JsonSerializable(typeof(ModelGatewayResult))]
[JsonSerializable(typeof(ModelGatewayStreamEvent))]
[JsonSerializable(typeof(ModelGatewayHealth))]
internal sealed partial class ModelGatewayJsonContext : JsonSerializerContext
{
}
