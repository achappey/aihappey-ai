using System.Text.Json;

namespace AIHappey.Core.Extensions;

public static class ResponseExtensions
{
    public static Dictionary<string, JsonElement> CreatePrimitiveProviderMetadata(
     this string providerId,
     object? data = null,
     decimal? costs = null)
    {
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [providerId] = JsonSerializer.SerializeToElement(
                data ?? new { },
                JsonSerializerOptions.Web)
        };

        if (costs.HasValue)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(
                new
                {
                    cost = costs.Value
                },
                JsonSerializerOptions.Web);
        }

        return providerMetadata;
    }

}
