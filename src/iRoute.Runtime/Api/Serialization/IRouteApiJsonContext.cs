using System.Text.Json;
using System.Text.Json.Serialization;
using iRoute.Common;

namespace iRoute.Runtime.Api;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ExecutionEvent))]
internal sealed partial class IRouteApiJsonContext : JsonSerializerContext
{
}
