using System.Security.Cryptography;
using System.Text;

namespace iRoute.Services;

internal static class PolicyReferences
{
    public static string CreateActionIdempotencyReference(
        string tenantId, string requestIdempotencyKey, string actionId, string capability)
    {
        var value = string.Join('\n', tenantId, requestIdempotencyKey, actionId, capability);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
