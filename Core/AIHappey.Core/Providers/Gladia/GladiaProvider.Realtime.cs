using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Gladia;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Gladia;

public partial class GladiaProvider : IModelProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = realtimeRequest.GetRealtimeProviderMetadata<GladiaRealtimeProviderMetadata>(GetIdentifier());
        metadata ??= new GladiaRealtimeProviderMetadata();
        metadata.Model = realtimeRequest.Model;

        var payload = JsonSerializer.SerializeToElement(metadata, JsonOpts);
        var resp = await _client.GetRealtimeResponse<GladiaiRealtimeResponse>(payload,
            relativeUrl: "v2/live",
            ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.Url,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
    }
}

public class GladiaiRealtimeResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("url")]
    public string Url { get; set; } = null!;

}