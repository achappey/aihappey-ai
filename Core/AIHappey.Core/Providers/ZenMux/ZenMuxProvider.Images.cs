using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZenMux;

public partial class ZenMuxProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        ApplyAuthHeader();
        var imageFiles = request.Files?.ToList() ?? [];
        var isEdit = imageFiles.Count > 0 || request.Mask is not null;
        var providerOptions = ReadImageProviderOptions(request.ProviderOptions);
        if (providerOptions is { } options)
        {
            if (options.TryGetProperty("partial_images", out _))
                throw new NotSupportedException("ZenMux partial_images is only available on the image streaming endpoints.");
            if (!isEdit && options.TryGetProperty("input_fidelity", out _))
                throw new NotSupportedException("ZenMux input_fidelity applies only to image edits.");
        }
        OpenAIImagesResponse response;

        if (isEdit)
        {
            response = await _client.OpenAICompatibleImageEditRequestAsync(new OpenAIImageEditRequest
            {
                Model = request.Model,
                Prompt = request.Prompt,
                Images = imageFiles.Select(ToOpenAIImageReference).ToArray(),
                Mask = request.Mask is null ? null : ToOpenAIImageReference(request.Mask),
                N = request.N,
                Size = request.Size,
                OutputFormat = ReadOptionString(providerOptions, "output_format") ?? "png",
                Background = ReadOptionString(providerOptions, "background"),
                InputFidelity = ReadOptionString(providerOptions, "input_fidelity"),
                Moderation = ReadOptionString(providerOptions, "moderation"),
                OutputCompression = ReadOptionInt(providerOptions, "output_compression"),
                PartialImages = ReadOptionInt(providerOptions, "partial_images"),
                Quality = ReadOptionString(providerOptions, "quality"),
                User = ReadOptionString(providerOptions, "user")
            }, cancellationToken: cancellationToken);
        }
        else
        {
            response = await _client.OpenAICompatibleImageGenerationRequestAsync(new OpenAIImageGenerationRequest
            {
                Model = request.Model,
                Prompt = request.Prompt,
                N = request.N,
                Size = request.Size,
                ResponseFormat = "b64_json",
                OutputFormat = ReadOptionString(providerOptions, "output_format") ?? "png",
                Background = ReadOptionString(providerOptions, "background"),
                Moderation = ReadOptionString(providerOptions, "moderation"),
                OutputCompression = ReadOptionInt(providerOptions, "output_compression"),
                Quality = ReadOptionString(providerOptions, "quality"),
                Style = ReadOptionString(providerOptions, "style"),
                User = ReadOptionString(providerOptions, "user")
            }, cancellationToken: cancellationToken);
        }

        var mimeType = ResolveImageMimeType(response.OutputFormat
            ?? ReadOptionString(providerOptions, "output_format"));
        var images = new List<string>();
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add($"data:{mimeType};base64,{image.B64Json}");
            }
            else if (Uri.TryCreate(image.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                using var downloadClient = _httpClientFactory.CreateClient();
                var bytes = await downloadClient.GetByteArrayAsync(uri, cancellationToken);
                images.Add($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}");
            }
        }

        if (images.Count == 0)
            throw new InvalidOperationException("ZenMux image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                JsonSerializer.SerializeToElement(response)),
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = new Dictionary<string, string>(),
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
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationStreamingAsync(options, cancellationToken: cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditStreamingAsync(options, cancellationToken: cancellationToken);
    }

    private static OpenAIImageReference ToOpenAIImageReference(ImageFile image)
    {
        if (string.Equals(image.Type, "file_id", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable CS0618
            return new OpenAIImageReference { FileId = image.Data };
#pragma warning restore CS0618
        }

        var value = image is ImageFileUrl imageUrl
            ? imageUrl.Url
            : image.Data ?? throw new ArgumentException("Image edit inputs require image data or a URL.");
        var url = string.Equals(image.Type, "url", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"data:{image.MediaType ?? "image/png"};base64,{value}";
        return new OpenAIImageReference { ImageUrl = url };
    }

    private static JsonElement? ReadImageProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("zenmux", out var options) != true)
            return null;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.zenmux must be an object.", nameof(providerOptions));

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "background", "input_fidelity", "moderation", "output_compression",
            "output_format", "partial_images", "quality", "style", "user"
        };
        var unsupported = options.EnumerateObject().FirstOrDefault(property => !supported.Contains(property.Name));
        if (unsupported.Name is not null)
            throw new NotSupportedException($"ZenMux image generation does not support provider option '{unsupported.Name}'.");
        return options;
    }

    private static string? ReadOptionString(JsonElement? options, string name)
        => options is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(name, out var item)
           && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static int? ReadOptionInt(JsonElement? options, string name)
        => options is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(name, out var item)
           && item.ValueKind == JsonValueKind.Number
           && item.TryGetInt32(out var number)
            ? number
            : null;

    private static string ResolveImageMimeType(string? outputFormat)
        => outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
}
