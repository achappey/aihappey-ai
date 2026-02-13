using AIHappey.Common.Model;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = JsonSerializer.SerializeToElement(new { });
        var resp = await _client.GetRealtimeResponse<DeepgramTokenResponse>(payload,
            relativeUrl: "v1/auth/grant",
            ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(resp.ExpiresIn).ToUnixTimeSeconds(),
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