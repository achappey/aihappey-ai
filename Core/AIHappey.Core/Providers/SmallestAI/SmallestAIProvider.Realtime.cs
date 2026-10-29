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
    private const string WavesTokenEndpoint = "https://api.smallest.ai/console/v1/auth/token";
    private const string WavesSttWebSocketEndpoint = "wss://api.smallest.ai/waves/v1/stt/live";

    public async Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        var agentId = NormalizeAgentId(realtimeRequest.Model);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("A SmallestAI Atoms agent model is required.", nameof(realtimeRequest));

        var model = agentId.Split('/')[0];
        if (model is PulseModel or PulseProModel or "pulse-2" or LightningV31Model or LightningV31ProModel
            or "lightning-v3.1" or "lightning-v3.1-pro")
            return await GetWavesTokenAsync(realtimeRequest, model, cancellationToken);
        if (model == "electron" || agentId.Contains('/'))
            throw new ArgumentException("SmallestAI realtime requires a Waves inference model or an Atoms agent ID.", nameof(realtimeRequest));

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

        if (result.Status == false || result.Data is null || string.IsNullOrWhiteSpace(result.Data.AccessToken))
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
                    SampleRate = result.Data.SampleRate,
                    InputAudioFormat = result.Data.InputAudioFormat,
                    OutputAudioFormat = result.Data.OutputAudioFormat
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private async Task<RealtimeResponse> GetWavesTokenAsync(RealtimeRequest request, string model, CancellationToken cancellationToken)
    {
        var options = request.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        var ttl = 300;
        if (options.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(options, "ttl_seconds", out var ttlElement)
            && (!ttlElement.TryGetInt32(out ttl) || ttl is < 30 or > 900))
            throw new ArgumentException("SmallestAI ttl_seconds must be an integer between 30 and 900.", nameof(request));

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, WavesTokenEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { ttl_seconds = ttl }), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("SmallestAI Waves token minting failed (401): API-key authentication was rejected on api.smallest.ai. No authentication fallback was attempted.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SmallestAI Waves token minting failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var token = ReadString(root, "access_token");
        if (token?.StartsWith("wat_", StringComparison.Ordinal) != true
            || ReadString(root, "token_type") != "Bearer"
            || !root.TryGetProperty("expires_in", out var expiresIn) || !expiresIn.TryGetInt32(out var seconds) || seconds is < 30 or > 900
            || !root.TryGetProperty("expires_at", out var expiresAt) || !expiresAt.TryGetDateTimeOffset(out var expiry)
            || expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("SmallestAI Waves token response did not include a valid access token and expiry.");

        // The token can be used for HTTP inference too. Pulse Pro has no streaming worker.
        string? websocketUrl = model is PulseModel or "pulse-2"
            ? $"{WavesSttWebSocketEndpoint}?model={model}"
            : model == PulseProModel ? null : "wss://api.smallest.ai/waves/v1/tts/live";
        return new RealtimeResponse
        {
            Value = token!,
            ExpiresAt = expiry.ToUnixTimeSeconds(),
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    websocket_url = websocketUrl,
                    expires_in = seconds,
                    token_type = "Bearer"
                })
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
        [JsonPropertyName("status")]
        public bool? Status { get; set; }

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

        [JsonPropertyName("input_audio_format")]
        public string? InputAudioFormat { get; set; }

        [JsonPropertyName("output_audio_format")]
        public string? OutputAudioFormat { get; set; }
    }

    private sealed class SmallestAIRealtimeProviderMetadata
    {
        [JsonPropertyName("websocket_url")]
        public string WebSocketUrl { get; set; } = null!;

        [JsonPropertyName("sample_rate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SampleRate { get; set; }

        [JsonPropertyName("input_audio_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InputAudioFormat { get; set; }

        [JsonPropertyName("output_audio_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputAudioFormat { get; set; }
    }
}

