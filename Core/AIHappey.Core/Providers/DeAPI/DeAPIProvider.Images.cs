using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<ImageResponse> DeapiImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var (width, height) = ResolveImageSize(request.Size, request.AspectRatio);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        string requestId;
        if (request.Files?.Any() == true)
        {
            using var form = new MultipartFormDataContent();
            var files = request.Files.ToArray();
            foreach (var file in files)
            {
                var fileContent = new ByteArrayContent(DecodeBase64Payload(file.Data));
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
                form.Add(fileContent, files.Length == 1 ? "image" : "images[]", "input-image" + GetImageExtension(file.MediaType));
            }

            var payload = new Dictionary<string, object?> { ["prompt"] = request.Prompt, ["model"] = request.Model };
            if (request.Seed is not null) payload["seed"] = request.Seed;
            var resolvedSize = ResolveImageSize(request.Size, request.AspectRatio);
            if (!string.IsNullOrWhiteSpace(request.Size) || !string.IsNullOrWhiteSpace(request.AspectRatio))
            {
                payload["width"] = resolvedSize.width;
                payload["height"] = resolvedSize.height;
            }
            MergeProviderMetadata(payload, metadata);
            AddFormValues(form, payload, "image", "images[]");
            requestId = await SubmitMultipartJobAsync("api/v2/images/edits", form, cancellationToken);
        }
        else
        {
            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt,
                ["model"] = request.Model
            };
            if (!string.IsNullOrWhiteSpace(request.Size) || !string.IsNullOrWhiteSpace(request.AspectRatio))
            {
                payload["width"] = width;
                payload["height"] = height;
            }
            if (request.Seed is not null) payload["seed"] = request.Seed;
            MergeProviderMetadata(payload, metadata);

            requestId = await SubmitJsonJobAsync("api/v2/images/generations", payload, cancellationToken);
        }

        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var resultUrl = GetResultUrl(completed)
            ?? throw new InvalidOperationException($"DeAPI image result_url missing for request {requestId}.");

        var (bytesOut, mediaType) = await DownloadResultAsync(resultUrl, MediaTypeNames.Image.Jpeg, cancellationToken);

        return new ImageResponse
        {
            Images = [Convert.ToBase64String(bytesOut).ToDataUrl(mediaType)],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static (int width, int height) ResolveImageSize(string? size, string? aspectRatio)
    {
        if (TryParseSize(size, out var w, out var h))
            return (w, h);

        if (!string.IsNullOrWhiteSpace(aspectRatio))
        {
            var inferred = aspectRatio.InferSizeFromAspectRatio(minWidth: 256, maxWidth: 1536, minHeight: 256, maxHeight: 1536);
            if (inferred is not null)
                return inferred.Value;
        }

        return (1024, 1024);
    }

    private static bool TryParseSize(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace(':', 'x').ToLowerInvariant();
        var parts = normalized.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
            return false;

        return width > 0 && height > 0;
    }

    private static string GetImageExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg"
        };
    }

    private static byte[] DecodeBase64Payload(string value)
    {
        var base64 = value;
        if (MediaContentHelpers.TryParseDataUrl(value, out _, out var parsedBase64))
            base64 = parsedBase64;

        return Convert.FromBase64String(base64);
    }
}

