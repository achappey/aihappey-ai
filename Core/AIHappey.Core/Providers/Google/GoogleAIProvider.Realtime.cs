using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string GoogleAuthTokensRelativeUrl = "v1beta/auth_tokens";
    private static readonly TimeSpan DefaultRealtimeTokenLifetime = TimeSpan.FromMinutes(30);

    public async Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        ApplyAuthHeader();

        var payload = BuildRealtimeTokenPayload(realtimeRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, GoogleAuthTokensRelativeUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
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

        if (!DateTimeOffset.TryParse(
                tokenResponse.ExpireTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expireTime))
        {
            throw new InvalidOperationException("Google realtime token response does not include a valid expireTime.");
        }

        return new RealtimeResponse
        {
            Value = tokenResponse.Name,
            ExpiresAt = expireTime.ToUnixTimeSeconds()
        };
    }

    private JsonObject BuildRealtimeTokenPayload(RealtimeRequest realtimeRequest)
    {
        var providerId = GetIdentifier();
        JsonObject payload;

        if (realtimeRequest.ProviderOptions is not null
            && realtimeRequest.ProviderOptions.TryGetValue(providerId, out var providerOptions)
            && providerOptions.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (providerOptions.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"providerOptions.{providerId} must be a JSON object.",
                    nameof(realtimeRequest));
            }

            payload = JsonNode.Parse(providerOptions.GetRawText())!.AsObject();
        }
        else
        {
            payload = new JsonObject
            {
                ["uses"] = 1,
                ["expireTime"] = DateTimeOffset.UtcNow
                    .Add(DefaultRealtimeTokenLifetime)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["liveConnectConstraints"] = new JsonObject
                {
                    ["config"] = new JsonObject
                    {
                        ["responseModalities"] = new JsonArray("TEXT"),
                        ["inputAudioTranscription"] = new JsonObject
                        {
                            ["languageCodes"] = new JsonArray()
                        }
                    }
                }
            };
        }

        var constraints = payload["liveConnectConstraints"] as JsonObject;
        if (constraints is null)
        {
            constraints = new JsonObject();
            payload["liveConnectConstraints"] = constraints;
        }

        constraints["model"] = NormalizeRealtimeModel(realtimeRequest.Model);
        return payload;
    }

    private static string NormalizeRealtimeModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A Google realtime model is required.", nameof(model));

        var normalized = model.Trim();
        var providerPrefix = GoogleExtensions.Identifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..];

        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"models/{normalized}";
    }

    private sealed class GoogleRealtimeTokenResponse
    {
        public string? Name { get; set; }

        public string? ExpireTime { get; set; }
    }
}
