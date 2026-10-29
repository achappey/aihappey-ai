using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => MintRealtimeTokenAsync(request.Model, request.ProviderOptions, cancellationToken);

    public async Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        var config = request.SessionConfig;
        if (config is not null && (config.Instructions is not null || config.Voice is not null || config.OutputModalities is not null
            || config.InputAudioFormat is not null || config.OutputAudioFormat is not null || config.InputAudioTranscription is not null
            || config.OutputAudioTranscription is not null || config.HasTurnDetection || config.Tools is not null))
            throw new NotSupportedException("Mistral transcription settings must be sent over the WebSocket before audio starts.");
        var token = await MintRealtimeTokenAsync(model, config?.ProviderOptions, cancellationToken);
        return new() { Token = token.Value, ExpiresAt = token.ExpiresAt,
            Url = "wss://api.mistral.ai/v1/audio/transcriptions/realtime?model=" + Uri.EscapeDataString(NormalizeRealtimeModel(model)) };
    }

    private static string NormalizeRealtimeModel(string model)
    {
        var local = model.StartsWith("mistral/", StringComparison.Ordinal) ? model[8..] : model;
        if (!RealtimeCapabilities.IsMistralRealtimeTranscription(local))
            throw new ArgumentException("Select a supported Mistral realtime transcription model.", nameof(model));
        return local;
    }

    private async Task<RealtimeResponse> MintRealtimeTokenAsync(string model, Dictionary<string, JsonElement>? providerOptions, CancellationToken ct)
    {
        model = NormalizeRealtimeModel(model);
        var payload = new Dictionary<string, object> { ["purpose"] = "realtime", ["model"] = model };
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object && options.TryGetProperty("ttl_seconds", out var ttl)
            && ttl.ValueKind != JsonValueKind.Null)
        {
            if (ttl.ValueKind != JsonValueKind.Number || !ttl.TryGetInt32(out var seconds) || seconds <= 0)
                throw new ArgumentException("Mistral ttl_seconds must be a positive integer.");
            payload["ttl_seconds"] = seconds;
        }

        ApplyAuthHeader();
        using var response = await _client.PostAsJsonAsync("v1/client/sessions", payload, ct);
        // Upstream bodies can contain credentials. Never include them in exception messages.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mistral realtime token creation failed (HTTP {(int)response.StatusCode}). Check the API key's create_client_session permission and selected model.");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            var secret = root.GetProperty("client_secret");
            var value = secret.GetProperty("value").GetString();
            var expiry = secret.TryGetProperty("expires_at", out var nested) ? nested : root.GetProperty("expires_at");
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("rt_", StringComparison.Ordinal) || value.Length <= 3
                || value.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
                || !expiry.TryGetDateTimeOffset(out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException();
            return new() { Value = value, ExpiresAt = expiresAt.ToUnixTimeSeconds() };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("Mistral returned an invalid or expired realtime client token.");
        }
    }
}
