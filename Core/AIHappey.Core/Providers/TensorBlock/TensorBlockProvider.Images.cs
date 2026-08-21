using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TensorBlock;

public partial class TensorBlockProvider
{
    private const string TensorBlockImageGenerationEndpoint = "v1/images/generations";
    private const string TensorBlockImageEditEndpoint = "v1/images/edits";

    private static readonly JsonSerializerOptions TensorBlockImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var payload = CreateTensorBlockVercelPayload(request.ProviderOptions);
        payload["model"] = JsonSerializer.SerializeToElement(request.Model);
        payload["prompt"] = JsonSerializer.SerializeToElement(request.Prompt);
        if (request.N.HasValue) payload["n"] = JsonSerializer.SerializeToElement(request.N.Value);
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = JsonSerializer.SerializeToElement(request.Size);
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = JsonSerializer.SerializeToElement(request.AspectRatio);
        if (request.Seed.HasValue) payload["seed"] = JsonSerializer.SerializeToElement(request.Seed.Value);

        var firstImage = request.Files?.FirstOrDefault();
        if (firstImage is null && request.Mask is not null)
            throw new ArgumentException("TensorBlock image edits require at least one input image.", nameof(request));
        var endpoint = firstImage is null ? TensorBlockImageGenerationEndpoint : TensorBlockImageEditEndpoint;
        if (firstImage is not null)
            payload["image"] = JsonSerializer.SerializeToElement(ToTensorBlockImage(firstImage));

        var result = await SendTensorBlockImageRequestAsync(payload, endpoint, cancellationToken);
        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:{image.MediaType};base64,{image.Base64}"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = result.Created.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(result.Created.Value).UtcDateTime
                    : DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CopyTensorBlockRawProperties(options.AdditionalProperties);
        AddTensorBlockGenerationFields(payload, options);
        var result = await SendTensorBlockImageRequestAsync(payload, TensorBlockImageGenerationEndpoint, cancellationToken);
        return ToTensorBlockOpenAIResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var payload = CopyTensorBlockRawProperties(options.AdditionalProperties);
        AddTensorBlockEditFields(payload, options);
        payload["image"] = JsonSerializer.SerializeToElement(await GetFirstTensorBlockOpenAIImageAsync(options, cancellationToken));

        var result = await SendTensorBlockImageRequestAsync(payload, TensorBlockImageEditEndpoint, cancellationToken);
        return ToTensorBlockOpenAIResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
        }
    }

    private async Task<TensorBlockImageResult> SendTensorBlockImageRequestAsync(
        Dictionary<string, JsonElement> payload,
        string endpoint,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, TensorBlockImageJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var response = await _client.PostAsync(endpoint, content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TensorBlock image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("TensorBlock image response did not contain a data array.");

        var images = new List<TensorBlockImage>();
        foreach (var item in data.EnumerateArray())
        {
            var mediaType = item.TryGetProperty("mime_type", out var mime) && mime.ValueKind == JsonValueKind.String
                ? mime.GetString() ?? MediaTypeNames.Image.Png
                : MediaTypeNames.Image.Png;
            var base64 = item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String
                ? b64.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(base64) && item.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                base64 = Convert.ToBase64String(await _client.GetByteArrayAsync(uri, cancellationToken));
            if (!string.IsNullOrWhiteSpace(base64))
                images.Add(new TensorBlockImage(
                    base64,
                    mediaType,
                    item.TryGetProperty("revised_prompt", out var revised) && revised.ValueKind == JsonValueKind.String
                        ? revised.GetString()
                        : null));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("TensorBlock image response did not contain any usable images.");

        var created = root.TryGetProperty("created", out var createdElement) && createdElement.TryGetInt64(out var value)
            ? value
            : (long?)null;
        return new TensorBlockImageResult(root, response.GetHeaders(), images, created);
    }

    private Dictionary<string, JsonElement> CreateTensorBlockVercelPayload(Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }
        return payload;
    }

    private static Dictionary<string, JsonElement> CopyTensorBlockRawProperties(Dictionary<string, JsonElement>? properties)
        => properties?.ToDictionary(property => property.Key, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    private static void AddTensorBlockGenerationFields(
        Dictionary<string, JsonElement> payload,
        OpenAIImageGenerationRequest options)
    {
        payload["model"] = JsonSerializer.SerializeToElement(options.Model);
        payload["prompt"] = JsonSerializer.SerializeToElement(options.Prompt);
        AddTensorBlockValue(payload, "background", options.Background);
        AddTensorBlockValue(payload, "moderation", options.Moderation);
        AddTensorBlockValue(payload, "n", options.N);
        AddTensorBlockValue(payload, "output_compression", options.OutputCompression);
        AddTensorBlockValue(payload, "output_format", options.OutputFormat);
        AddTensorBlockValue(payload, "quality", options.Quality);
        AddTensorBlockValue(payload, "response_format", options.ResponseFormat);
        AddTensorBlockValue(payload, "size", options.Size);
        AddTensorBlockValue(payload, "style", options.Style);
        AddTensorBlockValue(payload, "user", options.User);
    }

    private static void AddTensorBlockEditFields(
        Dictionary<string, JsonElement> payload,
        OpenAIImageEditRequest options)
    {
        payload["model"] = JsonSerializer.SerializeToElement(options.Model);
        payload["prompt"] = JsonSerializer.SerializeToElement(options.Prompt);
        AddTensorBlockValue(payload, "background", options.Background);
        AddTensorBlockValue(payload, "input_fidelity", options.InputFidelity);
        AddTensorBlockValue(payload, "moderation", options.Moderation);
        AddTensorBlockValue(payload, "n", options.N);
        AddTensorBlockValue(payload, "output_compression", options.OutputCompression);
        AddTensorBlockValue(payload, "output_format", options.OutputFormat);
        AddTensorBlockValue(payload, "quality", options.Quality);
        AddTensorBlockValue(payload, "size", options.Size);
        AddTensorBlockValue(payload, "user", options.User);
    }

    private static void AddTensorBlockValue<T>(Dictionary<string, JsonElement> payload, string name, T? value)
    {
        if (value is not null)
            payload[name] = JsonSerializer.SerializeToElement(value);
    }

    private static string ToTensorBlockImage(ImageFile image)
    {
        if (image.Type is "url" or "file_id")
            return image.Data;
        if (image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return image.Data;
        return $"data:{image.MediaType};base64,{image.Data}";
    }

    private static async Task<string> GetFirstTensorBlockOpenAIImageAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        if (options.ImageFiles?.FirstOrDefault() is { } file)
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return $"data:{file.ContentType ?? MediaTypeNames.Image.Png};base64,{Convert.ToBase64String(memory.ToArray())}";
        }

        var image = options.Images?.FirstOrDefault()?.ImageUrl;
        if (string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("TensorBlock image edits require an image URL or base64 image.", nameof(options));
        return image;
    }

    private static OpenAIImagesResponse ToTensorBlockOpenAIResponse(
        TensorBlockImageResult result,
        string? background,
        string? outputFormat,
        string? quality,
        string? size)
        => new()
        {
            Created = result.Created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Data = result.Images.Select(image => new OpenAIImageData
            {
                B64Json = image.Base64,
                RevisedPrompt = image.RevisedPrompt
            }).ToList()
        };

    private sealed record TensorBlockImage(string Base64, string MediaType, string? RevisedPrompt);

    private sealed record TensorBlockImageResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        List<TensorBlockImage> Images,
        long? Created);
}
