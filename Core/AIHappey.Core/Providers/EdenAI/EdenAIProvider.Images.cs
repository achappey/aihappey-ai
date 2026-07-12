using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider
{
    private static readonly JsonSerializerOptions EdenAIImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var files = request.Files?.Where(static file => file is not null).ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;

        if (isEdit && files.Count == 0)
            throw new ArgumentException("At least one image file is required for EdenAI image edits.", nameof(request));

        var payload = BuildEdenAIImagePayload(request, files, isEdit);
        var json = JsonSerializer.Serialize(payload, EdenAIImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            isEdit ? "v3/images/edits" : "v3/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"EdenAI image request failed ({(int)response.StatusCode})."
                : $"EdenAI image request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractEdenAIImages(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("EdenAI image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Usage = ExtractEdenAIImageUsage(root),
            ProviderMetadata = BuildEdenAIImageProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders()
            }
        };
    }

    private Dictionary<string, object?> BuildEdenAIImagePayload(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        bool isEdit)
    {
        var payload = CreateEdenAIImagePayloadFromProviderOptions(request);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;

        if (isEdit)
        {
            payload["images"] = files
                .Take(16)
                .Select(ToEdenAIImageReference)
                .ToList();

            if (request.Mask is not null)
                payload["mask"] = ToEdenAIImageReference(request.Mask);
        }

        return payload;
    }

    private Dictionary<string, object?> CreateEdenAIImagePayloadFromProviderOptions(ImageRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (request.ProviderOptions is null)
            return payload;

        foreach (var option in request.ProviderOptions)
        {
            if (!string.Equals(option.Key, GetIdentifier(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (option.Value.ValueKind != JsonValueKind.Object)
                return payload;

            foreach (var property in option.Value.EnumerateObject())
                payload[property.Name] = property.Value.Clone();

            return payload;
        }

        return payload;
    }

    private static Dictionary<string, object?> ToEdenAIImageReference(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.Equals(file.Type, "file_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Type, "fileId", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["file_id"] = file.Data
            };
        }

        return new Dictionary<string, object?>
        {
            ["image_url"] = NormalizeEdenAIImageInput(file)
        };
    }

    private static string NormalizeEdenAIImageInput(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private async Task<List<string>> ExtractEdenAIImages(JsonElement root, CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var value = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    images.Add(NormalizeEdenAIImageOutput(value, MediaTypeNames.Image.Png));

                continue;
            }

            if (!item.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                continue;

            var imageUrl = url.GetString();
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            images.Add(await NormalizeEdenAIImageUrl(imageUrl, cancellationToken));
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private async Task<string> NormalizeEdenAIImageUrl(string imageUrl, CancellationToken cancellationToken)
    {
        if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return imageUrl;

        using var imageResponse = await _client.GetAsync(imageUrl, cancellationToken);
        var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Failed to download EdenAI image from returned URL ({(int)imageResponse.StatusCode}).");

        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType
            ?? GuessEdenAIImageMediaType(imageUrl)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static string NormalizeEdenAIImageOutput(string value, string mediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        return value.ToDataUrl(mediaType);
    }

    private static string? GuessEdenAIImageMediaType(string imageUrl)
    {
        if (imageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (imageUrl.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
            || imageUrl.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (imageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return null;
    }

    private static ImageUsageData? ExtractEdenAIImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadEdenAIImageInt(usage, "input_tokens") ?? ReadEdenAIImageInt(usage, "inputTokens"),
            OutputTokens = ReadEdenAIImageInt(usage, "output_tokens") ?? ReadEdenAIImageInt(usage, "outputTokens"),
            TotalTokens = ReadEdenAIImageInt(usage, "total_tokens") ?? ReadEdenAIImageInt(usage, "totalTokens")
        };

        if (!usageData.InputTokens.HasValue
            && !usageData.OutputTokens.HasValue
            && !usageData.TotalTokens.HasValue)
        {
            return null;
        }

        return usageData;
    }

    private Dictionary<string, JsonElement>? BuildEdenAIImageProviderMetadata(JsonElement root)
    {
        var providerMetadata = new Dictionary<string, JsonElement>();
        var edenMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("data"))
                continue;

            edenMetadata[property.Name] = property.Value.Clone();
        }

        if (edenMetadata.Count > 0)
            providerMetadata[GetIdentifier()] = JsonSerializer.SerializeToElement(edenMetadata, JsonSerializerOptions.Web);

        if (root.TryGetProperty("cost", out var costElement) && TryGetDecimal(costElement, out var cost))
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return providerMetadata.Count == 0 ? null : providerMetadata;
    }

    private static int? ReadEdenAIImageInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }
}
