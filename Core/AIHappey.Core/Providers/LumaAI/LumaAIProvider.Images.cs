using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LumaAI;

public partial class LumaAIProvider
{
    private static readonly JsonSerializerOptions LumaImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record LumaGenerationStatus(string State, JsonElement Root);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var model = NormalizeModel(request.Model);
        if (model is not ("photon-1" or "photon-flash-1"))
            throw new NotSupportedException($"Luma image model '{request.Model}' is not supported.");

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = model,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio
        };

        var createJson = JsonSerializer.Serialize(payload, LumaImageJsonOptions);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "dream-machine/v1/generations/image")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Luma image request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var generationId = TryGetString(createRoot, "id")
            ?? throw new InvalidOperationException("Luma image response missing generation id.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollGenerationAsync(generationId, ct),
            isTerminal: r => r.State is "completed" or "failed",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.State == "failed")
        {
            var failureReason = TryGetString(final.Root, "failure_reason") ?? "Unknown failure.";
            throw new InvalidOperationException($"Luma image generation failed (id={generationId}): {failureReason}");
        }

        var imageUrl = TryGetString(final.Root, "assets", "image");
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidOperationException($"Luma image generation completed but no assets.image found (id={generationId}).");

        using var imageResp = await _client.GetAsync(imageUrl, cancellationToken);
        var imageBytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!imageResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(imageBytes);
            throw new InvalidOperationException($"Luma image download failed ({(int)imageResp.StatusCode}): {err}");
        }

        var mediaType = imageResp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(imageUrl)
            ?? MediaTypeNames.Image.Jpeg;
        var imageDataUrl = Convert.ToBase64String(imageBytes).ToDataUrl(mediaType);

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"dream-machine/v1/generations/{generationId}");
        using var deleteResp = await _client.SendAsync(deleteReq, cancellationToken);
        if (!deleteResp.IsSuccessStatusCode)
        {
            var deleteRaw = await deleteResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Luma generation delete failed ({(int)deleteResp.StatusCode}) for id={generationId}: {deleteRaw}");
        }

        return new ImageResponse
        {
            Images = [imageDataUrl],
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

    private async Task<LumaGenerationStatus> PollGenerationAsync(string generationId, CancellationToken cancellationToken)
    {
        using var pollResp = await _client.GetAsync($"dream-machine/v1/generations/{generationId}", cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Luma generation poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var state = (TryGetString(root, "state") ?? "unknown").Trim().ToLowerInvariant();
        return new LumaGenerationStatus(state, root);
    }

    private static string NormalizeModel(string model)
    {
        var value = model.Trim();
        if (value.StartsWith("lumaai/", StringComparison.OrdinalIgnoreCase))
            value = value["lumaai/".Length..];

        return value;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        return null;
    }
}
