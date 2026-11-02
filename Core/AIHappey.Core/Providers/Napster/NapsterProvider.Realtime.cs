using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;

namespace AIHappey.Core.Providers.Napster;

public partial class NapsterProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        var options = ReadOptions(realtimeRequest.ProviderOptions);
        var (response, details, expiry) = await CreateConnectionAsync(realtimeRequest.Model, options, cancellationToken);
        return new()
        {
            Value = RequiredString(response, "token"),
            ExpiresAt = expiry,
            ProviderMetadata = new() { [GetIdentifier()] = ConnectionMetadata(response, details) }
        };
    }

    public async Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Session);
        if (request.Transport is not LiveAdditionalRequestTransport { Type: "napster" } transport)
            throw new NotSupportedException("Napster Live sessions require the napster additional transport, not SDP or SIP.");
        if (transport.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("Supply Napster connection options in session extension data, not transport.");
        var config = request.Session;
        if (config.Audio is not null || config.Instructions is not null || config.Client is not null
            || config.Delegation is not null || config.Input is not null || config.Store is not null || config.Triggers is not null)
            throw new NotSupportedException("Use native Napster connection options in session extension data; agent configuration is inherited, not mapped from OpenAI Live settings.");
        var options = config.AdditionalProperties?.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) ?? [];
        var (response, details, _) = await CreateConnectionAsync(config.Model, options, cancellationToken);
        return new()
        {
            Session = new() { Id = RequiredString(response.GetProperty("connection"), "id") },
            Transport = new LiveAdditionalResponseTransport("napster")
            {
                AdditionalProperties = new()
                {
                    ["response"] = response,
                    ["connectionDetails"] = details
                }
            }
        };
    }

    private static Dictionary<string, JsonElement> ReadOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("napster", out var value) != true) return [];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.napster must be an object.");
        return value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement ConnectionMetadata(JsonElement response, JsonElement details)
        => JsonSerializer.SerializeToElement(new { response, connectionDetails = details });

    private async Task<(JsonElement Response, JsonElement Details, long Expiry)> CreateConnectionAsync(
        string model, Dictionary<string, JsonElement> options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.StartsWith("napster/", StringComparison.OrdinalIgnoreCase)) model = model[8..];
        string endpoint;
        var channel = "webrtc";
        if (model == "agent")
        {
            var agentId = RequiredOption(options, "agentId");
            if (agentId is "." or "..") throw new ArgumentException("Invalid Napster agentId.");
            options.Remove("agentId");
            if (options.ContainsKey("companionId") || options.ContainsKey("providerConfig"))
                throw new ArgumentException("Agent connections inherit companion and provider configuration from the agent.");
            channel = RequiredOption(options, "channelType");
            if (channel is not ("webrtc" or "websocket" or "kiosk"))
                throw new ArgumentException("Napster channelType must be webrtc, websocket or kiosk.");
            if (channel == "kiosk")
                throw new NotSupportedException("Napster kiosk connections require a paired Napster Station; direct-client kiosk provisioning is not supported.");
            if (options.TryGetValue("modality", out var modality))
            {
                if (modality.ValueKind != JsonValueKind.String || modality.GetString() is not ("audio" or "text" or "video"))
                    throw new ArgumentException("Napster modality must be audio, text or video.");
                if (modality.GetString() == "text" && channel != "websocket"
                    || modality.GetString() == "video" && channel == "websocket")
                    throw new ArgumentException("Napster text sessions require websocket; websocket does not support video.");
            }
            endpoint = $"public/agents/{Uri.EscapeDataString(agentId)}/connections";
        }
        else if (model == "companion")
        {
            _ = RequiredOption(options, "companionId");
            if (!options.TryGetValue("providerConfig", out var providerConfig) || providerConfig.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Napster companion connections require a providerConfig object.");
            if (options.ContainsKey("agentId") || options.ContainsKey("channelType") || options.ContainsKey("modality"))
                throw new ArgumentException("Companion connections use /public/connections (WebRTC). Use an agent for channelType or modality.");
            endpoint = "public/connections";
        }
        else throw new ArgumentException("Select the napster/agent or napster/companion model.");

        string? apiVersion = null;
        if (options.ContainsKey("apiVersion"))
        {
            apiVersion = RequiredOption(options, "apiVersion");
            if (apiVersion.Any(char.IsControl)) throw new ArgumentException("Invalid Napster API version.");
            options.Remove("apiVersion");
        }

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Napster API key.");
        if (key.Any(char.IsControl)) throw new InvalidOperationException("Invalid Napster API key.");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(options) };
        message.Headers.Add("X-Api-Key", key);
        message.Headers.Accept.ParseAdd("application/json");
        if (apiVersion is not null) message.Headers.Add("X-API-Version", apiVersion);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // A provisioning call creates a connection: never retry or echo secret-bearing upstream bodies.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Napster connection creation failed (HTTP {(int)response.StatusCode}).");

        JsonElement native;
        JsonElement details;
        try
        {
            native = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var token = RequiredString(native, "token");
            var bytes = Convert.FromBase64String(token);
            var json = new UTF8Encoding(false, true).GetString(bytes);
            details = JsonSerializer.Deserialize<JsonElement>(json);
            if (native.GetRawText().Contains(key, StringComparison.Ordinal) || json.Contains(key, StringComparison.Ordinal))
                throw new InvalidOperationException("Napster returned server credentials in connection details.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or DecoderFallbackException)
        {
            throw new InvalidOperationException("Napster returned an invalid connection response or encoded token.");
        }

        if (native.ValueKind != JsonValueKind.Object || !native.TryGetProperty("connection", out var connection)
            || connection.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Napster connection response is missing connection.");
        _ = RequiredString(connection, "id");
        if (!DateTimeOffset.TryParse(RequiredString(details, "expiresAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Napster returned an invalid or expired connection deadline.");
        if (channel == "websocket")
            ValidateConnectionUrl(RequiredString(details, "url"));
        else
        {
            if (!details.TryGetProperty("connection", out var rtc) || rtc.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Napster token is missing connection details.");
            _ = RequiredString(rtc, "id");
            ValidateConnectionUrl(RequiredString(rtc, "signalingEndpoint"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (native.Clone(), details.Clone(), expiresAt.ToUnixTimeSeconds());
    }

    private static void ValidateConnectionUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "wss")
            || string.IsNullOrWhiteSpace(url.Host) || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidOperationException("Napster returned an invalid secure connection URL.");
    }

    private static string RequiredString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()! : throw new InvalidOperationException($"Napster connection response is missing {name}.");

    private static string RequiredOption(Dictionary<string, JsonElement> options, string name)
        => options.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"Napster requires the {name} option.");
}
