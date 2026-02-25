using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.LumaAI;

public partial class LumaAIProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var hasImage = request.Image is not null;
        if (string.IsNullOrWhiteSpace(request.Prompt) && !hasImage)
            throw new ArgumentException("Prompt is required when image is not provided.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var model = request.Model;
        if (model is not ("ray-2" or "ray-flash-2"))
            throw new NotSupportedException($"Luma video model '{request.Model}' is not supported.");

        if (request.Image is not null && !request.Image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Luma video keyframe input must be an image/* media type.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt,
            ["resolution"] = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["duration"] = request.Duration is null ? null : $"{request.Duration.Value}s",
        };

        var providerOptions = GetLumaVideoProviderOptions(request, GetIdentifier());
        if (providerOptions?.Loop is not null)
            payload["loop"] = providerOptions.Loop.Value;

        if (request.Image is not null)
        {
            payload["keyframes"] = new Dictionary<string, object?>
            {
                ["frame0"] = new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["url"] = request.Image.Data.ToDataUrl(request.Image.MediaType)
                }
            };
        }

        var createJson = JsonSerializer.Serialize(payload, LumaImageJsonOptions);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "dream-machine/v1/generations/video")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Luma video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var generationId = TryGetString(createRoot, "id")
            ?? throw new InvalidOperationException("Luma video response missing generation id.");

        LumaGenerationStatus? final = null;
        string? deleteFailureMessage = null;

        try
        {
            final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => PollGenerationAsync(generationId, ct),
                isTerminal: r => r.State is "completed" or "failed",
                interval: TimeSpan.FromSeconds(2),
                timeout: TimeSpan.FromMinutes(10),
                maxAttempts: null,
                cancellationToken: cancellationToken);

            if (final.State == "failed")
            {
                var failureReason = TryGetString(final.Root, "failure_reason") ?? "Unknown failure.";
                throw new InvalidOperationException($"Luma video generation failed (id={generationId}): {failureReason}");
            }

            var videoUrl = TryGetString(final.Root, "assets", "video");
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new InvalidOperationException($"Luma video generation completed but no assets.video found (id={generationId}).");

            using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
            var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!videoResp.IsSuccessStatusCode)
            {
                var err = Encoding.UTF8.GetString(videoBytes);
                throw new InvalidOperationException($"Luma video download failed ({(int)videoResp.StatusCode}): {err}");
            }

            var mediaType = videoResp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(videoUrl)
                ?? "video/mp4";

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
                ProviderMetadata = new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = final.Root.Clone()
                },
                Response = new()
                {
                    Timestamp = now,
                    ModelId = request.Model,
                    Body = new Dictionary<string, object?>
                    {
                        ["submit"] = createRoot,
                        ["poll"] = final.Root.Clone(),
                        ["deleted"] = true,
                        ["generationId"] = generationId
                    }
                }
            };
        }
        finally
        {
            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"dream-machine/v1/generations/{generationId}");
            using var deleteResp = await _client.SendAsync(deleteReq, cancellationToken);
            if (!deleteResp.IsSuccessStatusCode)
            {
                var deleteRaw = await deleteResp.Content.ReadAsStringAsync(cancellationToken);
                deleteFailureMessage = $"Luma generation delete failed ({(int)deleteResp.StatusCode}) for id={generationId}: {deleteRaw}";
            }

            if (!string.IsNullOrWhiteSpace(deleteFailureMessage))
                throw new InvalidOperationException(deleteFailureMessage);
        }
    }

    private sealed class LumaVideoProviderOptions
    {
        public bool? Loop { get; set; }
    }

    private static LumaVideoProviderOptions? GetLumaVideoProviderOptions(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        try
        {
            return JsonSerializer.Deserialize<LumaVideoProviderOptions>(element.GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return default;
        }
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        return null;
    }
}
