using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    private const string DeepLRealtimeRelativeUrl = "v3/voice/realtime";
    private const string DefaultSourceMediaContentType = "audio/pcm;encoding=s16le;rate=16000";

    private static readonly JsonSerializerOptions RealtimeJsonOpts = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        ApplyAuthHeader();

        var payload = BuildRealtimePayload(realtimeRequest);

        using var req = new HttpRequestMessage(HttpMethod.Post, DeepLRealtimeRelativeUrl)
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL realtime session request failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<DeepLRealtimeSessionResponse>(body, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("DeepL realtime session response was empty.");

        if (string.IsNullOrWhiteSpace(parsed.Token))
            throw new InvalidOperationException("DeepL realtime session response does not include a token.");

        if (string.IsNullOrWhiteSpace(parsed.StreamingUrl))
            throw new InvalidOperationException("DeepL realtime session response does not include a streaming URL.");

        var providerMetadata = new DeepLRealtimeProviderMetadata
        {
            StreamingUrl = parsed.StreamingUrl,
            SessionId = parsed.SessionId
        };

        return new RealtimeResponse
        {
            Value = parsed.Token,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, RealtimeJsonOpts)
            }
        };
    }

    private static JsonElement BuildRealtimePayload(RealtimeRequest realtimeRequest)
    {
        if (realtimeRequest.ProviderOptions is not null
            && realtimeRequest.ProviderOptions.TryGetValue(nameof(DeepL).ToLowerInvariant(), out var providerPayload)
            && providerPayload.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return providerPayload;
        }

        return JsonSerializer.SerializeToElement(new DeepLRealtimeSessionRequest
        {
            SourceMediaContentType = DefaultSourceMediaContentType
        }, RealtimeJsonOpts);
    }

    private sealed class DeepLRealtimeSessionRequest
    {
        [JsonPropertyName("source_media_content_type")]
        public string SourceMediaContentType { get; set; } = DefaultSourceMediaContentType;
    }

    private sealed class DeepLRealtimeSessionResponse
    {
        [JsonPropertyName("streaming_url")]
        public string? StreamingUrl { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }
    }

    private sealed class DeepLRealtimeProviderMetadata
    {
        [JsonPropertyName("streaming_url")]
        public string? StreamingUrl { get; set; }

        [JsonPropertyName("session_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SessionId { get; set; }
    }
}
