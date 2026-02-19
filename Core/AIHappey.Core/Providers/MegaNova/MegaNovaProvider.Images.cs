using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MegaNova;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestMegaNova(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var metadata = imageRequest.GetProviderMetadata<MegaNovaImageProviderMetadata>(GetIdentifier());
        var warnings = new List<object>();

        if (imageRequest.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "MegaNova image generation currently returns a single image per request."
            });
        }

        int? width = null;
        int? height = null;
        var normalizedSize = imageRequest.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(normalizedSize))
        {
            width = new ImageRequest { Size = normalizedSize }.GetImageWidth();
            height = new ImageRequest { Size = normalizedSize }.GetImageHeight();
        }

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            var inferred = imageRequest.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
            }
            else
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "aspect_ratio",
                    details = "Aspect ratio could not be mapped to width/height and was ignored."
                });
            }
        }

        if (imageRequest.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "MegaNova image generation currently uses only the first input image. Additional files were ignored."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
            ["num_steps"] = metadata?.NumSteps,
            ["guidance_scale"] = metadata?.GuidanceScale,
            ["negative_prompt"] = metadata?.NegativePrompt,
            ["seed"] = imageRequest.Seed,
            ["width"] = width,
            ["height"] = height
        };

        var firstFile = imageRequest.Files?.FirstOrDefault();
        if (firstFile is not null)
            payload["image"] = firstFile.Data;

        if (imageRequest.Mask is not null)
            payload["mask"] = imageRequest.Mask.Data;

        var json = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generation")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var body = doc.RootElement.Clone();
        var images = ExtractImages(doc.RootElement);
        if (images.Count == 0)
            throw new Exception("No image data returned from MegaNova API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractUsage(doc.RootElement),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = body
            }
        };
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return images;
    }

    private static ImageUsageData? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData();

        if (usage.TryGetProperty("prompt_tokens", out var inputTokensEl) && inputTokensEl.TryGetInt32(out var inputTokens))
            usageData.InputTokens = inputTokens;

        if (usage.TryGetProperty("completion_tokens", out var outputTokensEl) && outputTokensEl.TryGetInt32(out var outputTokens))
            usageData.OutputTokens = outputTokens;

        if (usage.TryGetProperty("total_tokens", out var totalTokensEl) && totalTokensEl.TryGetInt32(out var totalTokens))
            usageData.TotalTokens = totalTokens;

        if (!usageData.InputTokens.HasValue && !usageData.OutputTokens.HasValue && !usageData.TotalTokens.HasValue)
            return null;

        return usageData;
    }
}

