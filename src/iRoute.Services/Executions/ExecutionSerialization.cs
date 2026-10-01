using System.Text.Json;

namespace iRoute.Services;

internal static class ExecutionSerialization
{
    internal static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);
}
