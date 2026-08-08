using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.LumaAI;

public partial class LumaAIProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
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
        if (model is not "ray-3.2")
            throw new NotSupportedException($"Luma video model '{request.Model}' is not supported.");

        if (request.Image is not null && !request.Image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Luma video keyframe input must be an image/* media type.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["type"] = "video",
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["video"] = new Dictionary<string, object?>
            {
                ["resolution"] = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
                ["duration"] = request.Duration is null ? null : $"{request.Duration.Value}s"
            }
        };

        var providerOptions = GetLumaVideoProviderOptions(request, GetIdentifier());
        if (providerOptions?.Loop is not null)
            ((Dictionary<string, object?>)payload["video"]!)["loop"] = providerOptions.Loop.Value;

        if (request.Image is not null)
        {
            ((Dictionary<string, object?>)payload["video"]!)["start_frame"] = new Dictionary<string, object?>
            {
                ["data"] = request.Image.Data,
                ["media_type"] = request.Image.MediaType
            };
        }

        var createJson = JsonSerializer.Serialize(payload, LumaImageJsonOptions);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/generations")
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

        return new VideoOperationStartResult
        {
            Operation = generationId,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { id = generationId, state = TryGetString(createRoot, "state") ?? "pending" }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        ApplyAuthHeader();
        var result = await PollGenerationAsync(operation, cancellationToken);
        var model = TryGetString(result.Root, "model");
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { id = operation, state = result.State });

        if (string.Equals(result.State, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var failureReason = TryGetString(result.Root, "failure_reason") ?? "Unknown failure.";
            return new VideoOperationErrorResult
            {
                Error = $"Luma video generation failed (id={operation}): {failureReason}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(result.State, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videoUrl = GetGenerationOutputUrl(result.Root, "video");
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Luma video generation completed but no video output was found (id={operation}).",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        // Luma returns a pre-signed object-storage URL. Sending the provider's
        // bearer token alongside its query-string signature invalidates it.
        using var videoResp = await _downloadClient.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Luma video download failed ({(int)videoResp.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = videoResp.Content.Headers.ContentType?.MediaType ?? GuessVideoMediaType(videoUrl) ?? "video/mp4",
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
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
