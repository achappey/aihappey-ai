using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azerion;

public partial class AzerionProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequestAzerion(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Mask is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "mask" });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Azerion uses explicit size; aspectRatio was ignored." });
        }

        if (request.N is > 1)
        {
            warnings.Add(new { type = "unsupported", feature = "n", details = "Azerion image generation returns a single image per request in this integration." });
        }

        var imageInputs = new List<string>();
        if (request.Files?.Any() == true)
        {
            foreach (var file in request.Files)
                imageInputs.Add(ToDataUrl(file));
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Trim(),
            ["prompt"] = request.Prompt,
            ["style"] = request.ProviderOptions?.TryGetValue("style", out var styleEl) == true && styleEl.ValueKind == JsonValueKind.String
                ? styleEl.GetString()
                : null,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["seed"] = request.Seed,
            ["response_format"] = "b64_json"
        };

        if (imageInputs.Count == 1)
            payload["image"] = imageInputs[0];
        else if (imageInputs.Count > 1)
            payload["image"] = imageInputs;

        var json = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azerion image generation failed ({(int)resp.StatusCode}): {raw}");
        }

        var images = ExtractB64ImagesAsDataUrls(raw);
        if (images.Count == 0)
            throw new InvalidOperationException("Azerion image generation response contained no images.");

        var usage = ExtractUsage(raw);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private static List<string> ExtractB64ImagesAsDataUrls(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop))
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(MediaTypeNames.Image.Jpeg));
        }

        return images;
    }

    private static ImageUsageData? ExtractUsage(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = usageEl.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : null,
            OutputTokens = usageEl.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : null,
            TotalTokens = usageEl.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : null
        };
    }

    private static string ToDataUrl(ImageFile file)
    {
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }
}
