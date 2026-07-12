using System.Globalization;
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

    public static long? TryGetHeaderInt64(this HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values)
            && !response.Content.Headers.TryGetValues(headerName, out values))
        {
            return null;
        }

        var value = values.FirstOrDefault();

        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
                ? result
                : null;
    }

}
