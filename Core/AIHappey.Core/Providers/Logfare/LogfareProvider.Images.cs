using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Logfare;

public partial class LogfareProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        using var response = isEdit
            ? await SendImageEditAsync(request, files, cancellationToken)
            : await SendImageGenerationAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Logfare image {(isEdit ? "edit" : "generation")} request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = ExtractImages(root);
        if (images.Count == 0)
            throw new InvalidOperationException("Logfare image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options, "v1/images/generations", cancellationToken))
            yield return streamEvent;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, "v1/images/edits", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options, "v1/images/edits", cancellationToken))
            yield return streamEvent;
    }

    private Task<HttpResponseMessage> SendImageGenerationAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeProviderModelId(request.Model),
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size,
            ["response_format"] = "b64_json"
        };
        AddProviderOptions(payload, request.ProviderOptions);
        return _client.PostAsync(
            "v1/images/generations",
            new StringContent(JsonSerializer.Serialize(payload, ImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
    }

    private Task<HttpResponseMessage> SendImageEditAsync(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            throw new ArgumentException("Logfare image edits require at least one input image.", nameof(request));

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(NormalizeProviderModelId(request.Model)), "model");
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent("b64_json"), "response_format");
        if (request.N.HasValue)
            form.Add(new StringContent(request.N.Value.ToString()), "n");
        if (!string.IsNullOrWhiteSpace(request.Size))
            form.Add(new StringContent(request.Size), "size");
        foreach (var file in files)
            form.Add(CreateImageContent(file), "image", "image" + GetImageExtension(file.MediaType));
        if (request.Mask is not null)
            form.Add(CreateImageContent(request.Mask), "mask", "mask" + GetImageExtension(request.Mask.MediaType));
        AddProviderOptions(form, request.ProviderOptions);
        return _client.PostAsync("v1/images/edits", form, cancellationToken);
    }

    private static void AddProviderOptions(
        Dictionary<string, object?> payload,
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("logfare", out var options) != true || options.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static void AddProviderOptions(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("logfare", out var options) != true || options.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in options.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var data = file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data[(file.Data.IndexOf(',') + 1)..]
            : file.Data;
        var content = new ByteArrayContent(Convert.FromBase64String(data));
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
        return content;
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        var images = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;
        foreach (var image in data.EnumerateArray())
        {
            if (image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                images.Add(url.GetString()!);
            else if (image.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(base64.GetString()))
            {
                var mimeType = image.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String
                    ? mime.GetString() ?? MediaTypeNames.Image.Png
                    : MediaTypeNames.Image.Png;
                images.Add($"data:{mimeType};base64,{base64.GetString()}");
            }
        }
        return images;
    }

    private static string GetImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png"
    };
}
