using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{
    private async Task<RealtimeTokenResponse> CreateGenericRealtimeTokenAsync(
        string model, RealtimeTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!RealtimeCapabilities.IsSpaceXAIRealtime(model))
            throw new NotSupportedException("This SpaceXAI model does not support realtime voice tokens.");
        var config = request.SessionConfig;
        var payload = RealtimeSessionMapping.Options(config, GetIdentifier());
        RealtimeSessionMapping.Protect(payload, model);
        payload.Remove("model");
        // Unlike OpenAI, xAI's token endpoint does not bind a session configuration.
        // Function definitions are returned to the browser for session.update.
        if (config is not null && (config.Instructions is not null || config.Voice is not null
            || config.OutputModalities is not null || config.InputAudioFormat is not null
            || config.OutputAudioFormat is not null || config.InputAudioTranscription is not null
            || config.OutputAudioTranscription is not null || config.HasTurnDetection))
            throw new ArgumentException("SpaceXAI session settings must be sent by the browser in session.update; tokens cannot bind them.");
        if (payload.Any(p => p.Key != "expires_after"))
            throw new ArgumentException("SpaceXAI token options support expires_after only; session settings belong to session.update.");
        ApplyAuthHeader();
        var result = await _client.PostSessionJsonAsync(payload, "v1/realtime/client_secrets", cancellationToken);
        return new RealtimeTokenResponse
        {
            Token = RealtimeSessionMapping.RequiredString(result, "value"),
            Url = "wss://api.x.ai/v1/realtime?model=" + Uri.EscapeDataString(model),
            ExpiresAt = RealtimeSessionMapping.Expiry(result, "expires_at"),
            Tools = config?.Tools
        };
    }
}
