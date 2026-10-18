using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Leonardo;

public sealed partial class LeonardoProvider
{
    private static readonly JsonSerializerOptions LeonardoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record GenerationStatus(string Status, JsonElement Root);

    private async Task<ImageResponse> ImageRequestInternal(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Leonardo image-to-image generation is not implemented.");

        var model = NormalizeModel(request.Model);
        var warnings = new List<object>();
        var payload = GetProviderOptions(request);
        payload["modelId"] = model;
        payload["prompt"] = request.Prompt;

        if (request.N.HasValue)
            payload["num_images"] = request.N.Value;

        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            if (TryParseSize(request.Size, out var width, out var height))
            {
                payload["width"] = width;
                payload["height"] = height;
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "size" });
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var created = await SendJsonAsync(HttpMethod.Post, "generations", payload, cancellationToken);
        var generationId = GetGenerationId(created)
            ?? throw new InvalidOperationException("Leonardo image generation response did not include a generation ID.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollGenerationAsync(generationId, ct),
            isTerminal: status => status.Status is "COMPLETE" or "FAILED",
            interval: TimeSpan.FromSeconds(2),
            timeout: null,
            maxAttempts: null,
            cancellationToken);

        if (final.Status == "FAILED")
            throw new InvalidOperationException($"Leonardo image generation failed (id={generationId}).");

        if (!TryGetProperty(final.Root, "generations_by_pk", out var generation)
            || !TryGetProperty(generation, "generated_images", out var generatedImages)
            || generatedImages.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Leonardo image generation completed without image results (id={generationId}).");

        var images = new List<string>();
        foreach (var generatedImage in generatedImages.EnumerateArray())
        {
            var imageUrl = GetString(generatedImage, "url");
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            images.Add(await DownloadImageAsync(imageUrl, cancellationToken));
        }

        if (images.Count == 0)
            throw new InvalidOperationException($"Leonardo image generation completed without image URLs (id={generationId}).");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(final.Root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<GenerationStatus> PollGenerationAsync(
        string generationId,
        CancellationToken cancellationToken)
    {
        var root = await SendJsonAsync(
            HttpMethod.Get,
            $"generations/{Uri.EscapeDataString(generationId)}",
            null,
            cancellationToken);

        if (!TryGetProperty(root, "generations_by_pk", out var generation))
            throw new InvalidOperationException($"Leonardo generation status response did not include generation data (id={generationId}).");

        var status = GetString(generation, "status")?.Trim().ToUpperInvariant();
        if (status is not ("PENDING" or "COMPLETE" or "FAILED"))
            throw new InvalidOperationException($"Leonardo returned an unsupported generation status for id={generationId}.");

        return new GenerationStatus(status, root);
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Leonardo API key.");

        using var request = CreateRequest(method, path, key);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, LeonardoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Leonardo request '{path}' failed ({(int)response.StatusCode}): {content}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    private async Task<string> DownloadImageAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Leonardo returned an invalid image URL.");

        using var response = await _downloadClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Leonardo image download failed ({(int)response.StatusCode}).",
                null,
                response.StatusCode);

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static Dictionary<string, object?> GetProviderOptions(ImageRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue("leonardo", out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private string NormalizeModel(string model)
    {
        var value = model.Trim();
        var prefix = GetIdentifier() + "/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    private static string? GetGenerationId(JsonElement root)
    {
        if ((!TryGetProperty(root, "sdGenerationJob", out var job)
             && !TryGetProperty(root, "sd_generation_job", out job))
            || job.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(job, "generationId") ?? GetString(job, "generation_id");
    }

    private static bool TryParseSize(string size, out int width, out int height)
    {
        width = 0;
        height = 0;
        var dimensions = size.Split('x', StringSplitOptions.TrimEntries);
        return dimensions.Length == 2
            && int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width > 0
            && height > 0;
    }
}
