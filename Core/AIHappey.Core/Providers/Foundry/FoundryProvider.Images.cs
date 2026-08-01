using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;

namespace AIHappey.Core.Providers.Foundry;

public partial class FoundryProvider
{
    private const string FoundryImageGenerationEndpoint = "openai/v1/images/generations?api-version=preview";
    private const string FoundryImageEditEndpoint = "openai/v1/images/edits?api-version=preview";
    private const string FoundryMaiImageGenerationEndpoint = "mai/v1/images/generations";
    private const string FoundryMaiImageEditEndpoint = "mai/v1/images/edits";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var files = request.Files?.Where(file => file is not null).ToArray() ?? [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var warnings = new List<object>();

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var isMaiModel = FoundryIsMaiImageModel(request.Model);
        var outputFormat = isMaiModel ? "png" : FoundryReadMetadataString(metadata, "output_format") ?? "png";
        var size = request.Size ?? (isMaiModel
            ? FoundryResolveMaiImageSize(request.AspectRatio, warnings)
            : FoundryResolveImageSize(request.AspectRatio, warnings));
        var options = files.Length == 0
            ? await FoundryGenerateImagesAsync(request, metadata, size, outputFormat, cancellationToken)
            : await FoundryEditImagesAsync(request, files, metadata, size, outputFormat, cancellationToken);
        var images = await FoundryNormalizeImagesAsync(options.Response, outputFormat, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Foundry image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = options.Response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = options.Response.Usage.InputTokens,
                OutputTokens = options.Response.Usage.OutputTokens,
                TotalTokens = options.Response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(options.Raw),
            Response = new HeaderResponseData
            {
                Timestamp = options.Response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(options.Response.Created).UtcDateTime
                    : DateTime.UtcNow,
                Headers = options.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        var requestOptions = FoundryForceBase64(options);
        if (FoundryIsMaiImageModel(requestOptions.Model))
            return (await FoundrySendMaiImageGenerationAsync(requestOptions, cancellationToken)).Response;

        return await _client.OpenAICompatibleImageGenerationRequestAsync(
            requestOptions,
            FoundryImageGenerationEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (FoundryIsMaiImageModel(options.Model))
        {
            var response = (await FoundrySendMaiImageGenerationAsync(FoundryForceBase64(options), cancellationToken)).Response;
            foreach (var image in response.Data ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(image.B64Json))
                    yield return new OpenAIImageGenerationCompleted
                    {
                        B64Json = image.B64Json,
                        CreatedAt = response.Created,
                        Size = options.Size,
                        OutputFormat = "png",
                        Usage = response.Usage
                    };
            }

            yield break;
        }

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            FoundryForceBase64(options),
            FoundryImageGenerationEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        var result = await FoundrySendOpenAIImageEditAsync(options, cancellationToken);
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);

        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    private async Task<FoundryImageResult> FoundryGenerateImagesAsync(
        ImageRequest request,
        JsonElement metadata,
        string? size,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var isMaiModel = FoundryIsMaiImageModel(request.Model);
        Dictionary<string, object?> payload;
        if (isMaiModel)
        {
            var (width, height) = FoundryResolveMaiDimensions(size);
            payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["width"] = width,
                ["height"] = height
            };
        }
        else
        {
            payload = FoundryMetadataToDictionary(metadata);
            payload["model"] = request.Model;
            payload["prompt"] = request.Prompt;
            payload["n"] = request.N;
            payload["size"] = size;
            payload["response_format"] = "b64_json";
        }

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            isMaiModel ? FoundryMaiImageGenerationEndpoint : FoundryImageGenerationEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        return await FoundrySendImageRequestAsync(httpRequest, "generation", cancellationToken);
    }

    private async Task<FoundryImageResult> FoundryEditImagesAsync(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        string? size,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var isMaiModel = FoundryIsMaiImageModel(request.Model);
        using var form = new MultipartFormDataContent();
        if (!isMaiModel)
            FoundryAddImageMetadata(form, metadata, "model", "prompt", "image", "mask", "size", "n", "output_format", "response_format");
        FoundryAddImageString(form, "model", request.Model);
        FoundryAddImageString(form, "prompt", request.Prompt);
        if (!isMaiModel)
        {
            FoundryAddImageString(form, "n", request.N?.ToString(CultureInfo.InvariantCulture));
            FoundryAddImageString(form, "size", size);
            FoundryAddImageString(form, "response_format", "b64_json");
        }

        for (var index = 0; index < files.Count; index++)
            form.Add(FoundryCreateImageContent(files[index]), "image", FoundryImageFileName(files[index], index));

        if (!isMaiModel && request.Mask is not null)
            form.Add(FoundryCreateImageContent(request.Mask), "mask", FoundryImageFileName(request.Mask, 0, "mask"));

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            isMaiModel ? FoundryMaiImageEditEndpoint : FoundryImageEditEndpoint) { Content = form };
        return await FoundrySendImageRequestAsync(httpRequest, "edit", cancellationToken);
    }

    private async Task<FoundryImageResult> FoundrySendOpenAIImageEditAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        var isMaiModel = FoundryIsMaiImageModel(options.Model);
        using var form = new MultipartFormDataContent();
        FoundryAddImageString(form, "model", options.Model);
        FoundryAddImageString(form, "prompt", options.Prompt);
        if (!isMaiModel)
        {
            FoundryAddImageString(form, "background", options.Background);
            FoundryAddImageString(form, "input_fidelity", options.InputFidelity);
            FoundryAddImageString(form, "moderation", options.Moderation);
            FoundryAddImageString(form, "n", options.N?.ToString(CultureInfo.InvariantCulture));
            FoundryAddImageString(form, "output_compression", options.OutputCompression?.ToString(CultureInfo.InvariantCulture));
            FoundryAddImageString(form, "output_format", options.OutputFormat ?? "png");
            FoundryAddImageString(form, "quality", options.Quality);
            FoundryAddImageString(form, "size", options.Size);
            FoundryAddImageString(form, "user", options.User);
            FoundryAddImageString(form, "response_format", "b64_json");

            foreach (var property in options.AdditionalProperties ?? [])
                FoundryAddImageString(form, property.Key, property.Value.ToString());
        }

        for (var index = 0; index < (options.ImageFiles?.Length ?? 0); index++)
        {
            var file = options.ImageFiles![index];
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.ContentType)
                ? MediaTypeNames.Application.Octet
                : file.ContentType);
            form.Add(content, "image", string.IsNullOrWhiteSpace(file.FileName) ? $"image-{index}.png" : file.FileName);
        }

        if (!isMaiModel && options.MaskFile is not null)
        {
            var content = new StreamContent(options.MaskFile.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(options.MaskFile.ContentType)
                ? MediaTypeNames.Application.Octet
                : options.MaskFile.ContentType);
            form.Add(content, "mask", string.IsNullOrWhiteSpace(options.MaskFile.FileName) ? "mask.png" : options.MaskFile.FileName);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            isMaiModel ? FoundryMaiImageEditEndpoint : FoundryImageEditEndpoint) { Content = form };
        return await FoundrySendImageRequestAsync(httpRequest, "edit", cancellationToken);
    }

    private async Task<FoundryImageResult> FoundrySendMaiImageGenerationAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken)
    {
        var (width, height) = FoundryResolveMaiDimensions(options.Size);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["prompt"] = options.Prompt,
            ["width"] = width,
            ["height"] = height
        };

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, FoundryMaiImageGenerationEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        return await FoundrySendImageRequestAsync(httpRequest, "generation", cancellationToken);
    }

    private async Task<FoundryImageResult> FoundrySendImageRequestAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Foundry image {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Foundry image {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var parsed = JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Foundry image {operation} response was empty.");
        return new FoundryImageResult(parsed, root, response.GetHeaders());
    }

    private async Task<List<string>> FoundryNormalizeImagesAsync(
        OpenAIImagesResponse response,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var mediaType = outputFormat.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };
        var images = new List<string>();

        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add(image.B64Json.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? image.B64Json
                    : image.B64Json.ToDataUrl(mediaType));
                continue;
            }

#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(image.Url))
            {
                var bytes = await _client.GetByteArrayAsync(image.Url, cancellationToken);
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
            }
#pragma warning restore CS0618
        }

        return images;
    }

    private static OpenAIImageGenerationRequest FoundryForceBase64(OpenAIImageGenerationRequest options)
    {
        options.ResponseFormat = "b64_json";
        options.Stream = false;
        options.OutputFormat ??= "png";
        return options;
    }

    private static Dictionary<string, object?> FoundryMetadataToDictionary(JsonElement metadata)
        => metadata.ValueKind == JsonValueKind.Object
            ? metadata.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone())
            : [];

    private static string? FoundryReadMetadataString(JsonElement metadata, string propertyName)
        => metadata.ValueKind == JsonValueKind.Object
           && metadata.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FoundryResolveImageSize(string? aspectRatio, List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return null;

        var size = aspectRatio.Trim().ToLowerInvariant() switch
        {
            "1:1" => "1024x1024",
            "2:3" or "3:4" or "9:16" => "1024x1536",
            "3:2" or "4:3" or "16:9" => "1536x1024",
            _ => null
        };
        if (size is null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = aspectRatio });
        return size;
    }

    private static bool FoundryIsMaiImageModel(string? model)
        => model?.StartsWith("MAI-Image-", StringComparison.OrdinalIgnoreCase) == true;

    private static string? FoundryResolveMaiImageSize(string? aspectRatio, List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return null;

        var size = aspectRatio.Trim().ToLowerInvariant() switch
        {
            "1:1" => "1024x1024",
            "2:3" => "768x1152",
            "3:4" => "768x1024",
            "9:16" => "768x1344",
            "3:2" => "1152x768",
            "4:3" => "1024x768",
            "16:9" => "1344x768",
            _ => null
        };
        if (size is null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = aspectRatio });
        return size;
    }

    private static (int Width, int Height) FoundryResolveMaiDimensions(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return (1024, 1024);

        var dimensions = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            throw new ArgumentException($"MAI image size '{size}' must use the '<width>x<height>' format.", nameof(size));

        if (width < 768 || height < 768 || (long)width * height > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(size), size,
                "MAI image width and height must each be at least 768 pixels and the total pixel count must not exceed 1,048,576.");

        return (width, height);
    }

    private static void FoundryAddImageMetadata(MultipartFormDataContent form, JsonElement metadata, params string[] excluded)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
            if (!excluded.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                FoundryAddImageString(form, property.Name, property.Value.ToString());
    }

    private static void FoundryAddImageString(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static ByteArrayContent FoundryCreateImageContent(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Foundry image edits require uploaded base64 image bytes; URL inputs are not supported.");

        var content = new ByteArrayContent(Convert.FromBase64String(file.Data.RemoveDataUrlPrefix()));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType);
        return content;
    }

    private static string FoundryImageFileName(ImageFile file, int index, string prefix = "image")
        => $"{prefix}-{index}.{file.MediaType?.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg => "jpg",
            "image/webp" => "webp",
            _ => "png"
        }}";

    private sealed record FoundryImageResult(
        OpenAIImagesResponse Response,
        JsonElement Raw,
        Dictionary<string, string> Headers);
}
