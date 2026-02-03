using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Linq;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static readonly JsonSerializerOptions GoogleVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (request.Image is not null)
            throw new InvalidOperationException("Google Veo video generation currently supports text-only requests.");

        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Google API key.");

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };

        var payload = BuildVideoPayload(request);
        var json = JsonSerializer.Serialize(payload, GoogleVideoJson);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"models/{request.Model}:predictLongRunning")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        createReq.Headers.Add("x-goog-api-key", key);

        using var createResp = await http.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var operationName = createRoot.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(operationName))
            throw new InvalidOperationException("Google video generation returned no operation name.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, operationName);
                pollReq.Headers.Add("x-goog-api-key", key);
                using var pollResp = await http.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Google video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            result => result.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True,
            interval: TimeSpan.FromSeconds(5),
            timeout: null,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Google video generation failed: {errorEl}");

        var videoUri = TryGetVideoUri(final);
        if (string.IsNullOrWhiteSpace(videoUri))
            throw new InvalidOperationException("Google video result contained no video uri.");

        using var downloadReq = new HttpRequestMessage(HttpMethod.Get, videoUri);
        downloadReq.Headers.Add("x-goog-api-key", key);
        using var downloadResp = await http.SendAsync(downloadReq, cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
        {
            var raw = await downloadResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google video download failed ({(int)downloadResp.StatusCode}): {raw}");
        }

        var videoBytes = await downloadResp.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = downloadResp.Content.Headers.ContentType?.MediaType ?? "video/mp4";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GoogleExtensions.Identifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["operation"] = createRoot.Clone(),
                ["result"] = final.Clone()
            }, JsonSerializerOptions.Web)
        };

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["instances"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["prompt"] = request.Prompt
                }
            }
        };

        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            parameters["aspectRatio"] = request.AspectRatio;

        if (request.Duration is not null)
            parameters["durationSeconds"] = request.Duration;

        if (parameters.Count > 0)
            payload["parameters"] = parameters;

        return payload;
    }

    private static string? TryGetVideoUri(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return null;

        if (!response.TryGetProperty("generateVideoResponse", out var generate) || generate.ValueKind != JsonValueKind.Object)
            return null;

        if (!generate.TryGetProperty("generatedSamples", out var samples) || samples.ValueKind != JsonValueKind.Array)
            return null;

        var first = samples.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return null;

        if (!first.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
            return null;

        if (!video.TryGetProperty("uri", out var uriEl) || uriEl.ValueKind != JsonValueKind.String)
            return null;

        return uriEl.GetString();
    }
}
