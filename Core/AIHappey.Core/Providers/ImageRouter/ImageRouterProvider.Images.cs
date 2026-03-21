using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.ImageRouter;

public partial class ImageRouterProvider
{
    private async Task<ImageResponse> ImageRequestImageRouter(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = BuildImageWarnings(request);
        var startedAt = DateTime.UtcNow;

        using var httpRequest = CreateImageRequestMessage(request, warnings);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ImageRouter API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var document = JsonDocument.Parse(raw);
        var completed = await EnsureTerminalImageRouterResponseAsync(document.RootElement.Clone(), cancellationToken);

        ThrowIfImageRouterError(completed, "image generation");

        var images = await ExtractImageOutputsAsync(completed, warnings, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("ImageRouter image generation returned no output.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = BuildProviderMetadata(completed),
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model,
                Body = completed
            }
        };
    }

    private HttpRequestMessage CreateImageRequestMessage(ImageRequest request, List<object> warnings)
    {
        var payload = BuildImagePayload(request, warnings);

        if (!HasImageUploads(request))
        {
            var body = JsonSerializer.Serialize(payload, ImageRouterJsonOptions);
            return new HttpRequestMessage(HttpMethod.Post, "v1/openai/images/generations")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        var multipart = new MultipartFormDataContent();

        foreach (var entry in payload)
            AddMultipartValue(multipart, entry.Key, entry.Value);

        foreach (var file in request.Files ?? [])
            multipart.Add(CreateFileContent(file), "image[]", GetFileName(file.MediaType, "image"));

        if (request.Mask is not null)
            multipart.Add(CreateFileContent(request.Mask), "mask[]", GetFileName(request.Mask.MediaType, "mask"));

        return new HttpRequestMessage(HttpMethod.Post, "v1/openai/images/edits")
        {
            Content = multipart
        };
    }

    private Dictionary<string, object?> BuildImagePayload(ImageRequest request, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        var size = ResolveImageSize(request, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        MergeRawProviderOptions(payload, request.ProviderOptions);

        payload["model"] = request.Model;
        payload["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        return payload;
    }

    private List<object> BuildImageWarnings(ImageRequest request)
    {
        var warnings = new List<object>();

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", property = "seed" });

        if (request.N.HasValue)
            warnings.Add(new { type = "unsupported", property = "n" });

        return warnings;
    }

    private static string? ResolveImageSize(ImageRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (TryResolveAspectRatioSize(request.AspectRatio, 1536, 1536, out var inferred))
        {
            warnings.Add(new
            {
                type = "mapped_property",
                property = "aspectRatio",
                mappedTo = "size",
                value = inferred
            });

            return inferred;
        }

        return null;
    }
}
