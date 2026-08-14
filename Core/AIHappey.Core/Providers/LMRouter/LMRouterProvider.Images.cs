using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.LMRouter;

public partial class LMRouterProvider
{
    private const string ImageGenerationEndpoint = "openai/v1/images/generations";
    private const string ImageEditEndpoint = "openai/v1/images/edits";

    private static readonly JsonSerializerOptions LMRouterImageJsonOptions = new(JsonSerializerDefaults.Web)
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
        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        using var response = isEdit
            ? await SendLMRouterImageEditAsync(request, files, cancellationToken)
            : await SendLMRouterImageGenerationAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LMRouter image {(isEdit ? "edit" : "generation")} failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ExtractLMRouterImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("LMRouter image response did not contain any images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
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
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, ImageGenerationEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            ImageGenerationEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, ImageEditEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
            ImageEditEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    private async Task<HttpResponseMessage> SendLMRouterImageGenerationAsync(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        var payload = GetLMRouterProviderOptions(request.ProviderOptions);
        payload["model"] = JsonSerializer.SerializeToElement(request.Model);
        payload["prompt"] = JsonSerializer.SerializeToElement(request.Prompt);
        payload["response_format"] = JsonSerializer.SerializeToElement("b64_json");
        if (request.N.HasValue) payload["n"] = JsonSerializer.SerializeToElement(request.N.Value);
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = JsonSerializer.SerializeToElement(request.Size);
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = JsonSerializer.SerializeToElement(request.AspectRatio);
        if (request.Seed.HasValue) payload["seed"] = JsonSerializer.SerializeToElement(request.Seed.Value);

        var content = new StringContent(
            JsonSerializer.Serialize(payload, LMRouterImageJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        return await _client.PostAsync(ImageGenerationEndpoint, content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendLMRouterImageEditAsync(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            throw new ArgumentException("LMRouter image edits require at least one input image.", nameof(request));

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        form.Add(new StringContent(request.Prompt, Encoding.UTF8), "prompt");
        form.Add(new StringContent("b64_json", Encoding.UTF8), "response_format");
        if (request.N.HasValue) form.Add(new StringContent(request.N.Value.ToString()), "n");
        if (!string.IsNullOrWhiteSpace(request.Size)) form.Add(new StringContent(request.Size), "size");
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) form.Add(new StringContent(request.AspectRatio), "aspect_ratio");
        if (request.Seed.HasValue) form.Add(new StringContent(request.Seed.Value.ToString()), "seed");

        foreach (var file in files)
            form.Add(CreateLMRouterImageContent(file), "image", "image" + GetLMRouterImageExtension(file.MediaType));
        if (request.Mask is not null)
            form.Add(CreateLMRouterImageContent(request.Mask), "mask", "mask" + GetLMRouterImageExtension(request.Mask.MediaType));

        foreach (var (name, value) in GetLMRouterProviderOptions(request.ProviderOptions))
        {
            if (name is "model" or "prompt" or "response_format" or "n" or "size" or "aspect_ratio" or "seed")
                continue;
            form.Add(new StringContent(LMRouterMultipartValue(value), Encoding.UTF8), name);
        }

        return await _client.PostAsync(ImageEditEndpoint, form, cancellationToken);
    }

    private Dictionary<string, JsonElement> GetLMRouterProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) != true || options.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static ByteArrayContent CreateLMRouterImageContent(ImageFile file)
    {
        if (file.Type is "url" or "file_id")
            throw new NotSupportedException("Generic LMRouter image edits require inline base64 image data.");

        var data = file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data[(file.Data.IndexOf(',') + 1)..]
            : file.Data;
        var content = new ByteArrayContent(Convert.FromBase64String(data));
        content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
        return content;
    }

    private async Task<List<string>> ExtractLMRouterImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            var mediaType = item.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String
                ? mime.GetString() ?? "image/png"
                : "image/png";
            if (item.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(base64.GetString()))
            {
                images.Add($"data:{mediaType};base64,{base64.GetString()}");
                continue;
            }

            if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                images.Add($"data:{mediaType};base64,{Convert.ToBase64String(await _client.GetByteArrayAsync(uri, cancellationToken))}");
        }
        return images;
    }

    private static string LMRouterMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static string GetLMRouterImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".image"
    };
}
