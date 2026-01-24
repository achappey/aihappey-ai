using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Replicate;
using AIHappey.Core.AI;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Replicate;

public sealed partial class ReplicateProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = imageRequest.Model;
        EnsureSupportedModel(model);

        var metadata = imageRequest.GetProviderMetadata<ReplicateImageProviderMetadata>(GetIdentifier());
        var preferWaitSeconds = ClampWaitSeconds(metadata?.PreferWaitSeconds);

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Replicate provider returns exactly 1 image in this backend." });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var inputImage = imageRequest.Files?.FirstOrDefault();
        if (imageRequest.Files?.Skip(1).Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Multiple input images not supported; used files[0]." });

        var (width, height) = ResolveWidthHeight(imageRequest);

        var input = new Dictionary<string, object?>
        {
            ["prompt"] = imageRequest.Prompt,
            ["width"] = width ?? 1024,
            ["height"] = height ?? 1024,
        };

        if (imageRequest.Seed is not null)
            input["seed"] = imageRequest.Seed;

        if (inputImage is not null)
            input["file"] = inputImage.Data.ToDataUrl(inputImage.MediaType);

        if (metadata?.InputOverrides is not null)
        {
            foreach (var kv in metadata.InputOverrides)
                input[kv.Key] = kv.Value;
        }

        var path = $"v1/models/{model}/predictions";

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { input }, JsonOpts),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        req.Headers.TryAddWithoutValidation("Prefer", $"wait={preferWaitSeconds}");
        if (!string.IsNullOrWhiteSpace(metadata?.CancelAfter))
            req.Headers.TryAddWithoutValidation("Cancel-After", metadata.CancelAfter);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("output", out var output) || output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            throw new Exception($"Replicate sync wait returned without output. status='{status ?? "unknown"}'.");
        }

        var urls = ExtractUrls(output);
        if (urls.Count == 0)
            throw new Exception("Replicate response did not contain any output image URLs.");

        var images = new List<string>();
        foreach (var url in urls)
            images.Add(await DownloadAsDataUrlAsync(url, cancellationToken));

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

    private static int ClampWaitSeconds(int? preferWaitSeconds)
    {
        if (preferWaitSeconds is null)
            return 60;

        return Math.Clamp(preferWaitSeconds.Value, 1, 60);
    }

    private static (int? width, int? height) ResolveWidthHeight(ImageRequest request)
    {
        var size = request.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var width = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageWidth();
        var height = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageHeight();

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
            }
        }

        return (width, height);
    }

    private static List<string> ExtractUrls(JsonElement output)
    {
        List<string> urls = [];
        Visit(output);
        return urls;

        void Visit(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    var s = el.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        return;

                    if (s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(s);
                        return;
                    }

                    if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
                        urls.Add(s);
                    return;

                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        Visit(item);
                    return;

                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                        Visit(prop.Value);
                    return;

                default:
                    return;
            }
        }
    }

    private async Task<string> DownloadAsDataUrlAsync(string urlOrDataUrl, CancellationToken ct)
    {
        if (urlOrDataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return urlOrDataUrl;

        using var resp = await _client.GetAsync(urlOrDataUrl, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Failed to download Replicate output '{urlOrDataUrl}': {(int)resp.StatusCode} {Encoding.UTF8.GetString(bytes)}");

        var mime = resp.Content.Headers.ContentType?.MediaType;
        mime ??= GuessMimeTypeFromUrl(urlOrDataUrl) ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mime);
    }

    private static string? GuessMimeTypeFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var provider = new FileExtensionContentTypeProvider();
            return provider.TryGetContentType(uri.AbsolutePath, out var mime) ? mime : null;
        }
        catch
        {
            return null;
        }
    }
}

