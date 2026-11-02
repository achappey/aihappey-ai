using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
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

        var model = NormalizeModelId(imageRequest.Model);
        EnsureSupportedModel(model);

        var metadata = imageRequest.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            throw new JsonException("Replicate image provider metadata must be a JSON object or null.");

        var preferWaitSeconds = ClampWaitSeconds(GetMetadataProperty<int?>(metadata, "preferWaitSeconds"));

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

        if (metadata is { ValueKind: JsonValueKind.Object } metadataObject)
        {
            foreach (var property in metadataObject.EnumerateObject())
            {
                if (!IsMetadataProperty(property.Name, "preferWaitSeconds", "cancelAfter", "inputOverrides"))
                    input[property.Name] = property.Value.Clone();
            }

            var inputOverrides = GetMetadataProperty(metadata, "inputOverrides");
            if (inputOverrides is { ValueKind: JsonValueKind.Object } overrideObject)
            {
                foreach (var property in overrideObject.EnumerateObject())
                    input[property.Name] = property.Value.Clone();
            }
            else if (inputOverrides is { ValueKind: not JsonValueKind.Null })
            {
                throw new JsonException("Replicate inputOverrides must be a JSON object or null.");
            }
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
        var cancelAfter = GetMetadataProperty<string>(metadata, "cancelAfter");
        if (!string.IsNullOrWhiteSpace(cancelAfter))
            req.Headers.TryAddWithoutValidation("Cancel-After", cancelAfter);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var output = await WaitForPredictionOutputAsync(doc.RootElement, cancellationToken);

        var urls = ExtractUrls(output);
        if (urls.Count == 0)
            throw new InvalidOperationException("Replicate response did not contain any output image URLs.");

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
                ModelId = model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private async Task<JsonElement> WaitForPredictionOutputAsync(
        JsonElement prediction,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);

        while (true)
        {
            if (prediction.TryGetProperty("output", out var output)
                && output.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return output.Clone();
            }

            var status = prediction.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (status is "failed" or "canceled" or "aborted")
            {
                var error = prediction.TryGetProperty("error", out var errorElement)
                    ? errorElement.ToString()
                    : null;
                throw new InvalidOperationException(
                    $"Replicate prediction failed with status '{status}': {error ?? "unknown error"}.");
            }

            var predictionId = prediction.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(predictionId))
            {
                throw new InvalidOperationException(
                    $"Replicate sync wait returned without output or a prediction id. status='{status ?? "unknown"}'.");
            }

            await Task.Delay(delay, cancellationToken);

            using var response = await _client.GetAsync(
                $"v1/predictions/{Uri.EscapeDataString(predictionId)}",
                cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{(int)response.StatusCode}: {raw}");

            using var document = JsonDocument.Parse(raw);
            prediction = document.RootElement.Clone();
            delay = delay < maxDelay
                ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5))
                : maxDelay;
        }
    }

    private static int ClampWaitSeconds(int? preferWaitSeconds)
    {
        if (preferWaitSeconds is null)
            return 60;

        return Math.Clamp(preferWaitSeconds.Value, 1, 60);
    }

    private static JsonElement? GetMetadataProperty(JsonElement? metadata, string propertyName)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } metadataObject)
            return null;

        JsonElement? result = null;
        foreach (var property in metadataObject.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                result = property.Value;
        }

        return result;
    }

    private static T? GetMetadataProperty<T>(JsonElement? metadata, string propertyName)
    {
        var property = GetMetadataProperty(metadata, propertyName);
        return property?.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool IsMetadataProperty(string propertyName, params string[] names)
        => names.Any(name => string.Equals(propertyName, name, StringComparison.OrdinalIgnoreCase));

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
