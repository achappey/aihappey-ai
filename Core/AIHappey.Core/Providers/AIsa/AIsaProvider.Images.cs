using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.AIsa;

public partial class AIsaProvider
{
    private const string AIsaImageGenerationsEndpoint = "v1/images/generations";
    private const string AIsaImageEditsEndpoint = "v1/images/edits";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        List<object> warnings = [];
        if (request.Seed is not null)
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
                throw new ArgumentException("AIsa image edits require at least one input image.", nameof(request));

            List<MemoryStream> streams = [];
            try
            {
                response = await OpenAIImageEditRequestAsync(CreateAIsaImageEditRequest(request, files, metadata, streams), cancellationToken);
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }
        }
        else
        {
            response = await OpenAIImageGenerationRequestAsync(CreateAIsaImageGenerationRequest(request, metadata), cancellationToken);
        }

        var images = await PersistAIsaImagesAsync(response, isEdit ? "image edit" : "image generation", cancellationToken);
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new()
            {
                Timestamp = response.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, AIsaImageGenerationsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
                           options, AIsaImageGenerationsEndpoint, cancellationToken))
            yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, AIsaImageEditsEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
                           options, AIsaImageEditsEndpoint, cancellationToken))
            yield return item;
    }

    private static OpenAIImageGenerationRequest CreateAIsaImageGenerationRequest(ImageRequest request, JsonElement metadata)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            Background = GetAIsaImageString(metadata, "background"),
            Moderation = GetAIsaImageString(metadata, "moderation"),
            OutputCompression = GetAIsaImageInt(metadata, "output_compression", "outputCompression"),
            OutputFormat = GetAIsaImageString(metadata, "output_format", "outputFormat"),
            PartialImages = GetAIsaImageInt(metadata, "partial_images", "partialImages"),
            Quality = GetAIsaImageString(metadata, "quality"),
            ResponseFormat = GetAIsaImageString(metadata, "response_format", "responseFormat"),
            Style = GetAIsaImageString(metadata, "style"),
            User = GetAIsaImageString(metadata, "user")
        };

    private static OpenAIImageEditRequest CreateAIsaImageEditRequest(
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
            ImageFiles = files.Select((file, index) => CreateAIsaFormFile(file, "image", $"image-{index + 1}", streams)).ToArray(),
            MaskFile = request.Mask is null ? null : CreateAIsaFormFile(request.Mask, "mask", "mask", streams),
            Background = GetAIsaImageString(metadata, "background"),
            InputFidelity = GetAIsaImageString(metadata, "input_fidelity", "inputFidelity"),
            Moderation = GetAIsaImageString(metadata, "moderation"),
            OutputCompression = GetAIsaImageInt(metadata, "output_compression", "outputCompression"),
            OutputFormat = GetAIsaImageString(metadata, "output_format", "outputFormat"),
            PartialImages = GetAIsaImageInt(metadata, "partial_images", "partialImages"),
            Quality = GetAIsaImageString(metadata, "quality"),
            User = GetAIsaImageString(metadata, "user")
        };

    private static IFormFile CreateAIsaFormFile(ImageFile file, string fieldName, string fallbackName, List<MemoryStream> streams)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException("AIsa generic image edits require base64 image data.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix()); }
        catch (FormatException exception) { throw new ArgumentException("Image data must be base64 encoded.", nameof(file), exception); }

        var stream = new MemoryStream(bytes, writable: false);
        streams.Add(stream);
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
        return new FormFile(stream, 0, stream.Length, fieldName, fallbackName + AIsaImageExtension(mediaType))
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private async Task<List<string>> PersistAIsaImagesAsync(OpenAIImagesResponse response, string operation, CancellationToken cancellationToken)
    {
        List<string> images = [];
        var fallbackMediaType = AIsaImageMediaType(response.OutputFormat);
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add(image.B64Json.ToDataUrl(fallbackMediaType));
                continue;
            }
            if (string.IsNullOrWhiteSpace(image.Url))
                continue;
            if (image.Url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(image.Url);
                continue;
            }

            using var download = await _client.GetAsync(image.Url, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to persist the short-lived AIsa image URL ({(int)download.StatusCode}).");
            var mediaType = download.Content.Headers.ContentType?.MediaType;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(string.IsNullOrWhiteSpace(mediaType) ? fallbackMediaType : mediaType));
        }

        return images.Count > 0 ? images : throw new InvalidOperationException($"AIsa {operation} response did not contain generated images.");
    }

    private static string AIsaImageMediaType(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
        "webp" => "image/webp",
        _ => MediaTypeNames.Image.Png
    };

    private static string AIsaImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        MediaTypeNames.Image.Jpeg or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png"
    };

    private static string? GetAIsaImageString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static int? GetAIsaImageInt(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (metadata.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
        return null;
    }
}
