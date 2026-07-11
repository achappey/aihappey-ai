using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.Extensions;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(realtimeRequest);

        var providerOptions = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = realtimeRequest.Model
        };

        MergeProviderOptions(payload, providerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/voice/sessions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CallMissed realtime session failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var token = ReadStringProperty(root, "value")
                    ?? ReadStringProperty(root, "token")
                    ?? ReadStringProperty(root, "livekit_token")
                    ?? ReadStringProperty(root, "client_secret");

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("CallMissed realtime response did not include a LiveKit token.");

        var wsUrl = ReadStringProperty(root, "ws_url")
                    ?? ReadStringProperty(root, "wsUrl")
                    ?? string.Empty;

        var expiresAt = ReadLongProperty(root, "expires_at")
            ?? (ReadLongProperty(root, "expires_in") is { } expiresIn
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

        return new RealtimeResponse
        {
            Value = token,
            ExpiresAt = expiresAt,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(new
                {
                    ws_url = wsUrl,
                    response = JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web)
                }),
        };
    }
}
