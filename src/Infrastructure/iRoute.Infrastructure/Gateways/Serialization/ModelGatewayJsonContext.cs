using System.Text.Json;
using System.Text.Json.Serialization;
using iRoute.Contracts;

namespace iRoute.Infrastructure;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ModelGatewayRequest))]
[JsonSerializable(typeof(ModelGatewayResult))]
[JsonSerializable(typeof(ModelGatewayStreamEvent))]
[JsonSerializable(typeof(ModelGatewayHealth))]
internal sealed partial class ModelGatewayJsonContext : JsonSerializerContext
{
}
