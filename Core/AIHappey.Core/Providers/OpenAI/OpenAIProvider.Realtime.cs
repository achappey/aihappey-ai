using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Extensions;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using System.Text.Json.Nodes;

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


    private static JsonElement? PutModelOnIncomingSessionType(JsonElement? sessionElement, string model)
    {
        if (sessionElement is not { ValueKind: JsonValueKind.Object })
            return sessionElement;

        var session = JsonNode.Parse(sessionElement.Value.GetRawText())!.AsObject();

        var type = session["type"]?.GetValue<string>();

        if (string.Equals(type, "realtime", StringComparison.OrdinalIgnoreCase))
        {
            session["model"] = model;
            return JsonSerializer.SerializeToElement(session, JsonOpts);
        }

        if (string.Equals(type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            var audio = EnsureObject(session, "audio");
            var input = EnsureObject(audio, "input");
            var transcription = EnsureObject(input, "transcription");

            transcription["model"] = model;

            return JsonSerializer.SerializeToElement(session, JsonOpts);
        }

        return sessionElement;
    }

    private static JsonObject EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject obj)
            return obj;

        obj = [];
        parent[name] = obj;
        return obj;
    }

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        var metadata = realtimeRequest.GetRealtimeProviderMetadata<OpenAiRealtimeProviderMetadata>(GetIdentifier());
  
        metadata?.Session = PutModelOnIncomingSessionType(
            metadata.Session,
            realtimeRequest.Model);
            
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
