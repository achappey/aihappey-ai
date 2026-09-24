using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    // @google/genai currently provisions ephemeral Live API tokens through the
    // v1alpha wire API. Its public `liveConnectConstraints` SDK option is
    // transformed client-side to the wire field `bidiGenerateContentSetup`.
    private const string GoogleAuthTokensRelativeUrl = "v1alpha/auth_tokens";

    public async Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        ApplyAuthHeader();

        var payload = GetRealtimeTokenPayload(realtimeRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, GoogleAuthTokensRelativeUrl)
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Google realtime token request failed ({(int)response.StatusCode}): {responseBody}");
        }

        GoogleRealtimeTokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize<GoogleRealtimeTokenResponse>(
                responseBody,
                JsonSerializerOptions.Web);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Google realtime token response was not valid JSON.", exception);
        }

        if (string.IsNullOrWhiteSpace(tokenResponse?.Name))
            throw new InvalidOperationException("Google realtime token response does not include a token name.");

        // Google's AuthToken marks expireTime as input-only and successful
        // responses may therefore contain only the token name. Preserve an
        // upstream value when present, otherwise map the exact client-owned
        // expiry that was raw-passed in the provisioning request.
        var expiryValue = !string.IsNullOrWhiteSpace(tokenResponse.ExpireTime)
            ? tokenResponse.ExpireTime
            : payload.TryGetProperty("expireTime", out var requestedExpiry)
                && requestedExpiry.ValueKind == JsonValueKind.String
                ? requestedExpiry.GetString()
                : null;

        if (!DateTimeOffset.TryParse(
                expiryValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expireTime))
        {
            throw new InvalidOperationException(
                "Google realtime token request does not include a valid client-supplied expireTime.");
        }

        return new RealtimeResponse
        {
            Value = tokenResponse.Name,
            ExpiresAt = expireTime.ToUnixTimeSeconds()
        };
    }

    private JsonElement GetRealtimeTokenPayload(RealtimeRequest realtimeRequest)
    {
        var providerId = GetIdentifier();

        if (realtimeRequest.ProviderOptions is null
            || !realtimeRequest.ProviderOptions.TryGetValue(providerId, out var providerOptions)
            || providerOptions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                $"providerOptions.{providerId} is required for Google realtime token provisioning.",
                nameof(realtimeRequest));
        }

        if (providerOptions.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"providerOptions.{providerId} must be a JSON object.",
                nameof(realtimeRequest));
        }

        // This endpoint is deliberately transport-only. The authenticated
        // client owns Google-specific expiry, field-mask, model, and constrained
        // Live setup values; do not invent, strip, or rewrite any of them here.
        return providerOptions;
    }

    private sealed class GoogleRealtimeTokenResponse
    {
        public string? Name { get; set; }

        public string? ExpireTime { get; set; }
    }
}
