using System.Text.Json;

namespace AIHappey.Core.Extensions;

public static class ResponseExtensions
{
    public static Dictionary<string, string> GetHeaders(
       this HttpResponseMessage responseMessage)
        => responseMessage.Headers
                  .Concat(responseMessage.Content.Headers)
                  .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                  .ToDictionary(
                      group => group.Key,
                      group => string.Join(", ", group.SelectMany(x => x.Value)),
                      StringComparer.OrdinalIgnoreCase
                  );

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
