using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Extensions;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.OpenAI;


public class OpenAiRealtimeResponse
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = null!;

    [JsonPropertyName("expires_at")]
    public int ExpiresAt { get; set; }

    [JsonPropertyName("session")]
    public JsonElement Session { get; set; }
}

public partial class OpenAIProvider 
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");


    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        var metadata = realtimeRequest.GetRealtimeProviderMetadata<OpenAiRealtimeProviderMetadata>(GetIdentifier());
        metadata ??= new OpenAiRealtimeProviderMetadata();
        metadata.Session ??= new RealtimeTranscriptionSession();
        metadata.Session.Audio ??= new RealtimeAudioConfig();
        metadata.Session.Audio.Input ??= new RealtimeAudioInputConfig();
        metadata.Session.Audio.Input.Transcription ??= new RealtimeTranscriptionConfig();
        metadata.Session.Audio.Input.Transcription.Model = realtimeRequest.Model;

        var payload = JsonSerializer.SerializeToElement(metadata, JsonOpts);
        var resp = await _client.GetRealtimeResponse<OpenAiRealtimeResponse>(payload, ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.Value,
            ExpiresAt = resp.ExpiresAt,
            ProviderMetadata = new Dictionary<string, JsonElement>()
            {
                {GetIdentifier(), JsonSerializer.SerializeToElement(new
                {
                    session = resp.Session
                })
                }
            }
        };
    }
}
