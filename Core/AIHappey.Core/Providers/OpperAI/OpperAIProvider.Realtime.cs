using System.Globalization;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        if (string.IsNullOrWhiteSpace(realtimeRequest.Model))
            throw new ArgumentException("Model is required.", nameof(realtimeRequest));

        ApplyAuthHeader();

        var payload = BuildOpperAIRealtimePayload(realtimeRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/realtime-sessions")
        {
            Content = CreateOpperAIJsonContent(payload)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI realtime ticket creation failed ({(int)response.StatusCode})."
                : $"OpperAI realtime ticket creation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var clientSecret = TryGetOpperAIString(root, "client_secret");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("OpperAI realtime ticket response did not include client_secret.");

        var expiresAt = TryGetOpperAIString(root, "expires_at");
        if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
            throw new InvalidOperationException("OpperAI realtime ticket response did not include a valid expires_at timestamp.");

        return new RealtimeResponse
        {
            Value = clientSecret,
            ExpiresAt = expiry.ToUnixTimeSeconds(),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                ws_url = TryGetOpperAIString(root, "ws_url"),
                response = root
            })
        };
    }

    private Dictionary<string, object?> BuildOpperAIRealtimePayload(RealtimeRequest realtimeRequest)
    {
        var providerOptions = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        var config = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (string.Equals(property.Name, "config", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException($"providerOptions.{GetIdentifier()}.config must be a JSON object.");

                    foreach (var configProperty in property.Value.EnumerateObject())
                        config[configProperty.Name] = configProperty.Value.Clone();
                    continue;
                }

                if (string.Equals(property.Name, "locked_fields", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "ttl_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }
        }
        else if (providerOptions.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.");
        }

        if (!config.ContainsKey("model"))
            config["model"] = realtimeRequest.Model;

        payload["config"] = config;
        return payload;
    }

}
