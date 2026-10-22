using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var requestOptions = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        var grantRequest = new Dictionary<string, int>();
        if (requestOptions.ValueKind == JsonValueKind.Object
            && requestOptions.TryGetProperty("ttl_seconds", out var ttlElement))
        {
            if (ttlElement.ValueKind != JsonValueKind.Number
                || !ttlElement.TryGetInt32(out var ttlSeconds)
                || ttlSeconds is < 1 or > 3600)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(realtimeRequest),
                    "Deepgram ttl_seconds must be an integer between 1 and 3600.");
            }

            grantRequest["ttl_seconds"] = ttlSeconds;
        }

        var payload = JsonSerializer.SerializeToElement(grantRequest);
        var resp = await _client.GetRealtimeResponse<DeepgramTokenResponse>(payload,
            relativeUrl: "v1/auth/grant",
            ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn).ToUnixTimeSeconds(),
        };
    }
}

public class DeepgramTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = null!;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

}