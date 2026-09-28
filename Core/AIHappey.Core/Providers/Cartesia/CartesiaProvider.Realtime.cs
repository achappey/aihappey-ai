using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
    private const string RealtimeApiVersion = "2026-08-14";

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        var model = realtimeRequest.Model?.Trim() ?? "";
        var parts = model.Split('/');
        var agent = model.StartsWith("agents/", StringComparison.OrdinalIgnoreCase)
            && model.Length > "agents/".Length
            && !model["agents/".Length..].Contains('/');
        var stt = parts.Length == 4 && parts[0] == "transcription" && parts[1] == "realtime"
            && (parts[2] is "auto" or "manual")
            && (parts[3] is "ink-2" or "ink-preview" or "ink-whisper")
            && !(parts[2] == "auto" && parts[3] == "ink-whisper");
        if (!agent && !stt)
            throw new ArgumentException("Cartesia realtime tokens require an agents/{id} or transcription/realtime/{auto|manual}/{model} model.", nameof(realtimeRequest));

        var seconds = 600;
        if (realtimeRequest.ProviderOptions?.TryGetValue("cartesia", out var options) == true && options.ValueKind == JsonValueKind.Object)
        {
            if (options.TryGetProperty("expires_in", out var expiry))
            {
                if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out seconds) || seconds is < 1 or > 3600)
                    throw new ArgumentOutOfRangeException(nameof(realtimeRequest), "Cartesia expires_in must be between 1 and 3600 seconds.");
            }
        }

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "access-token");
        ApplyVersionHeader(request, RealtimeApiVersion);
        request.Content = JsonContent.Create(new
        {
            grants = new { stt, agent },
            expires_in = seconds
        });
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cartesia access-token request failed ({(int)response.StatusCode}).");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("token", out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("Cartesia returned an empty access token.");

        return new RealtimeResponse
        {
            Value = value.GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds(),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["cartesia"] = JsonSerializer.SerializeToElement(new { cartesia_version = RealtimeApiVersion })
            }
        };
    }
}
