using System.Net.Mime;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Decart;

public partial class DecartProvider
{
    public async Task<ImageResponse> DecartImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (imageRequest.Files?.Skip(1).Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files.additional" });

        var endpoint = $"v1/generate/{imageRequest.Model}";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(imageRequest.Prompt), "prompt");
        form.Add(new StringContent(ResolveResolution(imageRequest)), "resolution");

        if (imageRequest.Files?.Any() == true)
        {
            var input = imageRequest.Files?.FirstOrDefault()
                ?? throw new InvalidOperationException("Lucy Pro I2I requires one input image in files[0].");

            var bytes = Convert.FromBase64String(input.Data);
            var data = new ByteArrayContent(bytes);
            data.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(input.MediaType);
            form.Add(data, "data", "input-image");
        }

        using var resp = await _client.PostAsync(endpoint, form, cancellationToken);
        var bytesOut = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytesOut);
            throw new Exception($"{resp.StatusCode}: {text}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Jpeg;
        var image = Convert.ToBase64String(bytesOut).ToDataUrl(mediaType);

        return new ImageResponse
        {
            Images = [image],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = new
                {
                    endpoint,
                    resolution = ResolveResolution(imageRequest),
                    contentType = mediaType,
                    bytes = bytesOut.Length
                }
            }
        };
    }

    private static string ResolveResolution(ImageRequest imageRequest)
    {
        if (TryResolveResolutionFromSize(imageRequest.Size, out var bySize))
            return bySize;

        if (TryResolveResolutionFromAspectRatio(imageRequest.AspectRatio, out var byAspectRatio))
            return byAspectRatio;

        return "720p";
    }

    private static bool TryResolveResolutionFromSize(string? size, out string resolution)
    {
        resolution = string.Empty;
        if (string.IsNullOrWhiteSpace(size))
            return false;

        var normalized = size.Trim().Replace(':', 'x');
        if (!TryParseSize(normalized, out var width, out var height))
            return false;

        if ((width == 480 && height == 832) || (width == 832 && height == 480))
        {
            resolution = "480p";
            return true;
        }

        if ((width == 720 && height == 1280) || (width == 1280 && height == 720))
        {
            resolution = "720p";
            return true;
        }

        return false;
    }

    private static bool TryResolveResolutionFromAspectRatio(string? aspectRatio, out string resolution)
    {
        resolution = string.Empty;

        if (string.IsNullOrWhiteSpace(aspectRatio))
            return false;

        var inferred = aspectRatio.InferSizeFromAspectRatio();
        if (inferred is null)
            return false;

        return TryResolveResolutionFromSize($"{inferred.Value.width}x{inferred.Value.height}", out resolution);
    }

    private static bool TryParseSize(string value, out int width, out int height)
    {
        width = 0;
        height = 0;

        var parts = value.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
            return false;

        return width > 0 && height > 0;
    }
}

