using System.Text;
using System.Text.Json;

namespace iRoute.Runtime;

internal static class TokenEstimator
{
    public static int Estimate(JsonElement value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        return Math.Max(1, (int)Math.Ceiling(bytes / 4d));
    }
}
