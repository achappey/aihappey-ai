using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Xpectrum;

/// <summary>
/// Voice provisioning: exchanges the server-side API key for a LiveKit room token. The voice agent's configuration is
/// owned by the Xpectrum app, so provider-neutral session settings are rejected rather than silently ignored.
/// </summary>
public sealed partial class XpectrumProvider
{
    public async Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.SessionConfig;
        if (config is not null && (config.Instructions is not null || config.Voice is not null || config.OutputModalities is not null
            || config.InputAudioFormat is not null || config.OutputAudioFormat is not null || config.InputAudioTranscription is not null
            || config.OutputAudioTranscription is not null || config.HasTurnDetection || config.Tools is not null))
            throw new NotSupportedException("Xpectrum voice sessions use the agent configured in the Xpectrum console; only sessionConfig.providerOptions.xpectrum is forwarded.");
        var (native, url, expiresAt) = await GenerateVoiceTokenAsync(config?.ProviderOptions, cancellationToken);
        return new() { Token = Str(native, "token")!, Url = url, ExpiresAt = expiresAt };
    }

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        var (native, _, expiresAt) = await GenerateVoiceTokenAsync(realtimeRequest.ProviderOptions, cancellationToken);
        return new()
        {
            Value = Str(native, "token")!,
            ExpiresAt = expiresAt ?? 0,
            ProviderMetadata = new() { [GetIdentifier()] = native }
        };
    }

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Xpectrum voice uses LiveKit rooms, not SDP. Use the realtime token endpoint.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Xpectrum voice uses LiveKit rooms, not OpenAI Live sessions. Use the realtime token endpoint.");

    private async Task<(JsonElement Native, string Url, long? ExpiresAt)> GenerateVoiceTokenAsync(
        Dictionary<string, JsonElement>? providerOptions, CancellationToken cancellationToken)
    {
        string? body = null;
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind is not JsonValueKind.Null)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.xpectrum must be an object.");
            body = options.GetRawText();
        }
        var key = ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "voice/tokens/generate");
        // The official SDK authenticates voice provisioning with x-api-key; Bearer is sent as documented for all endpoints.
        message.Headers.TryAddWithoutValidation("x-api-key", key);
        if (body is not null)
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Provisioning bodies may echo credentials; surface only the status and parsed error code.
            var error = CreateError(response.StatusCode, "", response.Headers.RetryAfter);
            var (code, _) = ReadError(text);
            if (code is not null)
                error.Data["code"] = code;
            throw error;
        }
        if (text.Contains(key, StringComparison.Ordinal))
            throw new InvalidOperationException("Xpectrum voice provisioning response contained the API key and was discarded.");
        JsonElement native;
        try
        {
            native = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Xpectrum returned an invalid voice token response.");
        }
        if (native.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(Str(native, "token")))
            throw new InvalidOperationException("Xpectrum voice token response is missing token.");
        var url = Str(native, "livekit_url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "wss")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Xpectrum returned an invalid secure LiveKit URL.");
        long? expiresAt = null;
        if (native.TryGetProperty("expires_in", out var expires))
        {
            var raw = expires.ValueKind == JsonValueKind.Number ? expires.GetRawText() : expires.ValueKind == JsonValueKind.String ? expires.GetString() : null;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds();
        }
        return (native, url!, expiresAt);
    }
}
