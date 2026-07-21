using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private const string AtomsRegisterCallEndpoint = "https://api.smallest.ai/atoms/v1/conversation/register-call";
    private const string AtomsWebSocketEndpoint = "wss://api.smallest.ai/atoms/v1/agent/connect";

    public async Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        var agentId = NormalizeAgentId(realtimeRequest.Model);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("A SmallestAI Atoms agent model is required.", nameof(realtimeRequest));

        ApplyAuthHeader();

        var request = new SmallestAIRegisterCallRequest
        {
            AgentId = agentId,
            Mode = "webcall",
            Variables = ReadRealtimeVariables(realtimeRequest)
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, AtomsRegisterCallEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} realtime call registration failed ({(int)response.StatusCode}): {body}");

        var result = JsonSerializer.Deserialize<SmallestAIRegisterCallResponse>(body, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"{ProviderName} realtime call registration response was empty.");

        if (result.Data is null || string.IsNullOrWhiteSpace(result.Data.AccessToken))
            throw new InvalidOperationException($"{ProviderName} realtime call registration response did not include an access token.");

        var expiresIn = result.Data.ExpiresIn > 0 ? result.Data.ExpiresIn : 30;
        var token = result.Data.AccessToken;

        return new RealtimeResponse
        {
            Value = token,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new SmallestAIRealtimeProviderMetadata
                {
                    WebSocketUrl = $"{AtomsWebSocketEndpoint}?token={Uri.EscapeDataString(token)}",
                    SampleRate = result.Data.SampleRate
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private string? NormalizeAgentId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var normalized = model.Trim();
        var prefix = $"{GetIdentifier()}/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private Dictionary<string, object?>? ReadRealtimeVariables(RealtimeRequest realtimeRequest)
    {
        var providerOptions = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(providerOptions, "variables", out var variablesElement)
            || variablesElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (variablesElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SmallestAI realtime variables must be an object.", nameof(realtimeRequest));

        var variables = new Dictionary<string, object?>();
        foreach (var variable in variablesElement.EnumerateObject())
        {
            variables[variable.Name] = variable.Value.ValueKind switch
            {
                JsonValueKind.String => variable.Value.GetString(),
                JsonValueKind.Number when variable.Value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number when variable.Value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ArgumentException(
                    $"SmallestAI realtime variable '{variable.Name}' must be a string, number, or boolean.",
                    nameof(realtimeRequest))
            };
        }

        return variables;
    }

    private sealed class SmallestAIRegisterCallRequest
    {
        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; } = null!;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "webcall";

        [JsonPropertyName("variables")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Variables { get; set; }
    }

    private sealed class SmallestAIRegisterCallResponse
    {
        [JsonPropertyName("data")]
        public SmallestAIRegisterCallResponseData? Data { get; set; }
    }

    private sealed class SmallestAIRegisterCallResponseData
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("sample_rate")]
        public int? SampleRate { get; set; }
    }

    private sealed class SmallestAIRealtimeProviderMetadata
    {
        [JsonPropertyName("websocket_url")]
        public string WebSocketUrl { get; set; } = null!;

        [JsonPropertyName("sample_rate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SampleRate { get; set; }
    }
}

