using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private async Task<RealtimeTokenResponse> CreateGenericRealtimeTokenAsync(
        string model, RealtimeTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!RealtimeCapabilities.IsGoogleLive(model))
            throw new NotSupportedException("This Google model does not support Live tokens.");
        var payload = RealtimeSessionMapping.GooglePayload(model, request.SessionConfig, DateTimeOffset.UtcNow);
        var tools = RealtimeSessionMapping.GoogleTools(payload["bidiGenerateContentSetup"]?["tools"]);
        ApplyAuthHeader();
        // The generic adapter uses the current beta API. The legacy alpha/raw flow is unchanged.
        var result = await _client.PostSessionJsonAsync(payload, "v1beta/auth_tokens", cancellationToken);
        var expiry = result.ContainsKey("expireTime")
            ? RealtimeSessionMapping.GoogleExpiry(result, "expireTime")
            : RealtimeSessionMapping.GoogleExpiry(payload, "expireTime");
        return new RealtimeTokenResponse
        {
            Token = RealtimeSessionMapping.RequiredString(result, "name"),
            Url = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContentConstrained",
            ExpiresAt = expiry.ToUnixTimeSeconds(),
            Tools = tools
        };
    }
}
