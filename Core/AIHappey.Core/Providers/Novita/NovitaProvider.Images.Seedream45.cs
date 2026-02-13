using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Novita;
using System.Net.Mime;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    private static readonly JsonSerializerOptions Seedream45Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestSeedream45(
         ImageRequest request,
         CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = request.GetProviderMetadata<NovitaImageProviderMetadata>(GetIdentifier());
        var seedream = providerMetadata?.Seedream45;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "Seedream 4.5 expects explicit size; aspectRatio was ignored."
            });
        }

        if (request.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Seedream 4.5 returns a single image per request in this integration."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        var size = request.Size?.Trim();
        if (string.IsNullOrWhiteSpace(size))
        {
            size = "2048x2048";
            warnings.Add(new
            {
                type = "default",
                feature = "size",
                details = "No size provided; defaulted to 2048x2048."
            });
        }

        var imageInputs = new List<string>();
        if (request.Files?.Any() == true)
        {
            foreach (var file in request.Files.Take(14))
                imageInputs.Add(ToDataUrl(file));

            if (request.Files.Skip(14).Any())
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = "Seedream 4.5 supports up to 14 input images; extra files were ignored."
                });
            }
        }

        var payload = new Seedream45Request
        {
            Size = size,
            Image = imageInputs.Count > 0 ? imageInputs : null,
            Prompt = request.Prompt,
            Watermark = seedream?.Watermark,
            OptimizePromptOptions = seedream?.OptimizePromptOptions,
            SequentialImageGeneration = seedream?.SequentialImageGeneration,
            SequentialImageGenerationOptions = seedream?.SequentialImageGenerationOptions
        };

        var json = JsonSerializer.Serialize(payload, Seedream45Json);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.novita.ai/v3/seedream-4.5"))
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractSeedreamImagesAsync(raw, cancellationToken);
        if (images.Count == 0)
            throw new Exception("Novita Seedream 4.5 returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private async Task<List<string>> ExtractSeedreamImagesAsync(string raw, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<string>();

        foreach (var item in imagesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(value);
                continue;
            }

            if (IsHttpUrl(value))
            {
                results.Add(await DownloadAsDataUrlAsync(value, ct));
                continue;
            }

            results.Add(value.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return results;
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task<string> DownloadAsDataUrlAsync(string url, CancellationToken ct)
    {
        var client = _factory.CreateClient();
        using var resp = await client.GetAsync(url, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Failed to download Seedream 4.5 image '{url}': {(int)resp.StatusCode} {Encoding.UTF8.GetString(bytes)}");

        var mime = resp.Content.Headers.ContentType?.MediaType;
        mime ??= GuessMimeTypeFromUrl(url) ?? MediaTypeNames.Image.Png;

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

    private static bool IsSeedream45Model(string? model)
           => string.Equals(model, "seedream-4.5", StringComparison.OrdinalIgnoreCase);

    private static string ToDataUrl(ImageFile file)
    {
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private sealed class Seedream45Request
    {
        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("image")]
        public List<string>? Image { get; set; }

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = null!;

        [JsonPropertyName("watermark")]
        public bool? Watermark { get; set; }

        [JsonPropertyName("optimize_prompt_options")]
        public NovitaOptimizePromptOptions? OptimizePromptOptions { get; set; }

        [JsonPropertyName("sequential_image_generation")]
        public string? SequentialImageGeneration { get; set; }

        [JsonPropertyName("sequential_image_generation_options")]
        public NovitaSequentialImageGenerationOptions? SequentialImageGenerationOptions { get; set; }
    }

}
