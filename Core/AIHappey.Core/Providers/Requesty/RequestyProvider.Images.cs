using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
{
    private static readonly JsonSerializerOptions RequestyImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        string? outputFormat = null;
        string? quality = null;
        string? background = null;

        var providerOptions = imageRequest.ProviderOptions;
        if (providerOptions is not null &&
            providerOptions.TryGetValue(GetIdentifier(), out var requestyOptions) &&
            requestyOptions.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(requestyOptions, out var outputFormatValue, "output_format", "outputFormat"))
                outputFormat = outputFormatValue;

            if (TryGetString(requestyOptions, out var qualityValue, "quality"))
                quality = qualityValue;

            if (TryGetString(requestyOptions, out var backgroundValue, "background"))
                background = backgroundValue;
        }

        var files = imageRequest.Files?.ToList() ?? [];
        if (files.Count > 0 || imageRequest.Mask is not null)
        {
            var rawEdit = await SendImageEditRequestAsync(
                imageRequest,
                files,
                outputFormat,
                quality,
                background,
                cancellationToken);

            var editMediaType = ToImageMediaType(outputFormat);
            var editImages = ExtractB64ImagesAsDataUrls(rawEdit, editMediaType);
            if (editImages.Count == 0)
                throw new Exception("Requesty returned no edited images.");

            return new ImageResponse
            {
                Images = editImages,
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = now,
                    ModelId = imageRequest.Model.ToModelId(GetIdentifier()) 
                }
            };
        }

        var payload = new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            n = imageRequest.N,
            size = imageRequest.Size,
            quality,
            background,
            output_format = outputFormat,
            response_format = "b64_json"
        };

        var json = JsonSerializer.Serialize(payload, RequestyImageJson);

        using var response = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"{response.StatusCode}: {raw}");

        var mediaType = ToImageMediaType(outputFormat);
        var images = ExtractB64ImagesAsDataUrls(raw, mediaType);
        if (images.Count == 0)
            throw new Exception("Requesty returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private async Task<string> SendImageEditRequestAsync(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        string? outputFormat,
        string? quality,
        string? background,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            throw new ArgumentException("Requesty image edits require at least one input image in files.", nameof(request));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent("b64_json"), "response_format");

        if (request.N.HasValue)
            form.Add(new StringContent(request.N.Value.ToString()), "n");

        if (!string.IsNullOrWhiteSpace(request.Size))
            form.Add(new StringContent(request.Size), "size");

        if (!string.IsNullOrWhiteSpace(quality))
            form.Add(new StringContent(quality), "quality");

        if (!string.IsNullOrWhiteSpace(background))
            form.Add(new StringContent(background), "background");

        if (!string.IsNullOrWhiteSpace(outputFormat))
            form.Add(new StringContent(outputFormat), "output_format");

        for (var i = 0; i < files.Count; i++)
            form.Add(CreateRequestyImageContent(files[i]), "image[]", $"image-{i + 1}{GetRequestyImageExtension(files[i].MediaType)}");

        if (request.Mask is not null)
            form.Add(CreateRequestyImageContent(request.Mask), "mask", $"mask{GetRequestyImageExtension(request.Mask.MediaType)}");

        using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"{response.StatusCode}: {raw}");

        return raw;
    }

    private static ByteArrayContent CreateRequestyImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Image.Png
                : file.MediaType);

        return content;
    }

    private static string GetRequestyImageExtension(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

    private static string ToImageMediaType(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };

    private static List<string> ExtractB64ImagesAsDataUrls(string rawJson, string mediaType)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(mediaType));
        }

        return images;
    }

    private static bool TryGetString(JsonElement obj, out string? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;

            value = el.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}

