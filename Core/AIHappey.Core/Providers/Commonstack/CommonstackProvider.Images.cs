using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Commonstack;

public partial class CommonstackProvider
{
    private const string ImageGenerationsEndpoint = "v1/openai/images/generations";
    private const string ImageEditsEndpoint = "v1/openai/images/edits";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var isEdit = files.Count > 0 || request.Mask is not null;

        OpenAIImagesResponse response;
        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("Commonstack image edits require at least one input image.", nameof(request));

            var streams = new List<MemoryStream>();
            try
            {
                response = await OpenAIImageEditRequestAsync(
                    CreateGenericEditRequest(request, files, metadata, streams),
                    cancellationToken);
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }
        }
        else
        {
            response = await OpenAIImageGenerationRequestAsync(
                CreateGenericGenerationRequest(request, metadata),
                cancellationToken);
        }

        var images = ExtractImages(response, isEdit ? "image edit" : "image generation");
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ToImageUsage(response.Usage),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new()
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
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

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            ImageGenerationsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            ImageGenerationsEndpoint,
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

        return _client.OpenAICompatibleImageEditRequestAsync(
            options,
            ImageEditsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
            ImageEditsEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken = default)
        => this.FromImageRequest(options, cancellationToken);

    private OpenAIImageGenerationRequest CreateGenericGenerationRequest(ImageRequest request, JsonElement metadata)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            Background = GetString(metadata, "background"),
            Moderation = GetString(metadata, "moderation"),
            OutputCompression = GetInt32(metadata, "output_compression", "outputCompression"),
            OutputFormat = GetString(metadata, "output_format", "outputFormat"),
            PartialImages = GetInt32(metadata, "partial_images", "partialImages"),
            Quality = GetString(metadata, "quality"),
            ResponseFormat = GetString(metadata, "response_format", "responseFormat"),
            Style = GetString(metadata, "style"),
            User = GetString(metadata, "user")
        };

    private static OpenAIImageEditRequest CreateGenericEditRequest(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<MemoryStream> streams)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ImageFiles = files
                .Select((file, index) => CreateFormFile(file, "image", $"image-{index + 1}", streams))
                .ToArray(),
            MaskFile = request.Mask is null ? null : CreateFormFile(request.Mask, "mask", "mask", streams),
            Background = GetString(metadata, "background"),
            InputFidelity = GetString(metadata, "input_fidelity", "inputFidelity"),
            Moderation = GetString(metadata, "moderation"),
            OutputCompression = GetInt32(metadata, "output_compression", "outputCompression"),
            OutputFormat = GetString(metadata, "output_format", "outputFormat"),
            PartialImages = GetInt32(metadata, "partial_images", "partialImages"),
            Quality = GetString(metadata, "quality"),
            User = GetString(metadata, "user")
        };

    private static IFormFile CreateFormFile(ImageFile file, string fieldName, string fallbackName, List<MemoryStream> streams)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Commonstack generic image edits require base64 image data; remote URL image inputs are not supported.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Image data must be base64 encoded.", nameof(file), exception);
        }

        var stream = new MemoryStream(bytes, writable: false);
        streams.Add(stream);
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
        var fileName = fallbackName + GetImageExtension(mediaType);
        return new FormFile(stream, 0, stream.Length, fieldName, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private static List<string> ExtractImages(OpenAIImagesResponse response, string operation)
    {
        var mediaType = GetImageMediaType(response.OutputFormat);
        var images = response.Data?
            .Select(image => !string.IsNullOrWhiteSpace(image.B64Json)
                ? image.B64Json.ToDataUrl(mediaType)
                : image.Url)
            .Where(image => !string.IsNullOrWhiteSpace(image))
            .Cast<string>()
            .ToList() ?? [];

        return images.Count > 0
            ? images
            : throw new InvalidOperationException($"Commonstack {operation} response did not contain generated images.");
    }

    private static ImageUsageData? ToImageUsage(OpenAIImageUsage? usage)
        => usage is null
            ? null
            : new ImageUsageData
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens
            };

    private static string GetImageMediaType(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private static string GetImageExtension(string mediaType)
        => mediaType.Trim().ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Jpeg or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

    private static string? GetString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static int? GetInt32(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (metadata.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }
}
