using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private static readonly JsonSerializerOptions ApertisImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var endpoint = files.Count > 0 ? "v1/images/edits" : "v1/images/generations";

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Apertis image endpoints do not document seed." });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Apertis image endpoints use size. aspectRatio was not mapped." });

        if (files.Count == 0 && request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "Mask is only sent for Apertis image edits." });

        using var httpRequest = files.Count > 0
            ? BuildApertisImageEditRequest(request, files, metadata, warnings)
            : BuildApertisImageGenerationRequest(request, metadata);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Apertis image request failed ({(int)response.StatusCode}) [{endpoint}]."
                : $"Apertis image request failed ({(int)response.StatusCode}) [{endpoint}]: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractApertisImagesAsync(root, metadata, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Apertis image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractApertisImageUsage(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                endpoint,
                body = root
            }),
            Response = new()
            {
                Timestamp = ReadApertisUnixTimestamp(root, "created") ?? now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private HttpRequestMessage BuildApertisImageGenerationRequest(ImageRequest request, JsonElement metadata)
    {
        var payload = ApertisJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.N.HasValue)
            payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ApertisImageJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };
    }

    private static HttpRequestMessage BuildApertisImageEditRequest(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        AddApertisMetadataFormFields(form, metadata);
        AddApertisFormString(form, "model", request.Model);
        AddApertisFormString(form, "prompt", request.Prompt);
        AddApertisFormString(form, "size", request.Size);

        if (request.N.HasValue)
            AddApertisFormString(form, "n", request.N.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Apertis image edits document a single image field. Only the first file was sent." });

        form.Add(CreateApertisImageFileContent(files[0]), "image", $"image{ApertisImageExtension(files[0].MediaType)}");

        if (request.Mask is not null)
            form.Add(CreateApertisImageFileContent(request.Mask), "mask", $"mask{ApertisImageExtension(request.Mask.MediaType)}");

        return new HttpRequestMessage(HttpMethod.Post, "v1/images/edits")
        {
            Content = form
        };
    }

    private async Task<List<string>> ExtractApertisImagesAsync(JsonElement root, JsonElement metadata, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        var fallbackMediaType = ApertisImageMediaTypeFromFormat(ApertisTryGetString(root, "output_format") ?? ApertisTryGetString(metadata, "output_format"));

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var mediaType = ApertisImageMediaTypeFromFormat(
                ApertisTryGetString(item, "output_format")
                ?? ApertisTryGetString(item, "format")
                ?? ApertisTryGetString(item, "mime_type")
                ?? fallbackMediaType);

            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(mediaType));

                continue;
            }

            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                {
                    images.Add(url);
                    continue;
                }

                using var imageResp = await _client.GetAsync(url, cancellationToken);
                var bytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!imageResp.IsSuccessStatusCode || bytes.Length == 0)
                    throw new InvalidOperationException($"Failed to download Apertis image from returned URL ({(int)imageResp.StatusCode}).");

                var downloadedMediaType = imageResp.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(downloadedMediaType) || !downloadedMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    downloadedMediaType = mediaType;

                images.Add(Convert.ToBase64String(bytes).ToDataUrl(downloadedMediaType));
            }
        }

        return images;
    }

    private static ImageUsageData? ExtractApertisImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ApertisTryGetInt(usage, "input_tokens"),
            OutputTokens = ApertisTryGetInt(usage, "output_tokens"),
            TotalTokens = ApertisTryGetInt(usage, "total_tokens")
        };

        return usageData.InputTokens.HasValue || usageData.OutputTokens.HasValue || usageData.TotalTokens.HasValue
            ? usageData
            : null;
    }

    private static ByteArrayContent CreateApertisImageFileContent(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Apertis image edits require uploaded image bytes. URL image inputs are not supported by this adapter for edits.");

        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType);
        return content;
    }

    private static void AddApertisMetadataFormFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
            AddApertisFormString(form, property.Name, ApertisJsonElementToFormValue(property.Value));
    }

    private static void AddApertisFormString(MultipartFormDataContent form, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static string? ApertisJsonElementToFormValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

    private static string ApertisImageMediaTypeFromFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or MediaTypeNames.Image.Jpeg => MediaTypeNames.Image.Jpeg,
            "webp" or "image/webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private static string ApertisImageExtension(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };
}
