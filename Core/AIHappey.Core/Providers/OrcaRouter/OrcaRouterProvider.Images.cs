using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using System.Net.Mime;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.OrcaRouter;

public partial class OrcaRouterProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";
    private const string ImageEditsEndpoint = "v1/images/edits";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var isEdit = files.Count > 0 || request.Mask is not null;

        OpenAIImagesResponse response;
        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("OrcaRouter image edits require at least one input image.", nameof(request));

            var streams = new List<MemoryStream>();
            try
            {
                response = await OpenAIImageEditRequestAsync(
                    CreateEditRequest(request, files, metadata, streams),
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
                CreateGenerationRequest(request, metadata),
                cancellationToken);
        }

        var images = response.Data?
            .Select(image => !string.IsNullOrWhiteSpace(image.B64Json)
                ? image.B64Json.ToDataUrl(GetImageMediaType(response.OutputFormat))
                : image.Url)
            .Where(image => !string.IsNullOrWhiteSpace(image))
            .Cast<string>()
            .ToList() ?? [];

        if (images.Count == 0)
            throw new InvalidOperationException("OrcaRouter image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
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


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            ImageGenerationsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            ImageGenerationsEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageEditRequestAsync(
            options,
            ImageEditsEndpoint,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            options,
            ImageEditsEndpoint,
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageVariationRequest();
        return this.FromImageRequest(options, cancellationToken);
    }

    private static OpenAIImageGenerationRequest CreateGenerationRequest(ImageRequest request, JsonElement metadata)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            Quality = GetString(metadata, "quality"),
            ResponseFormat = GetString(metadata, "response_format", "responseFormat"),
            Style = GetString(metadata, "style"),
            User = GetString(metadata, "user")
        };

    private static OpenAIImageEditRequest CreateEditRequest(
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
            ImageFiles = files.Select((file, index) => CreateFormFile(file, "image", $"image-{index + 1}", streams)).ToArray(),
            MaskFile = request.Mask is null ? null : CreateFormFile(request.Mask, "mask", "mask", streams),
            Quality = GetString(metadata, "quality"),
            User = GetString(metadata, "user")
        };

    private static IFormFile CreateFormFile(ImageFile file, string fieldName, string name, List<MemoryStream> streams)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException("OrcaRouter image edits require uploaded image data; remote image URLs are not supported.");

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
        return new FormFile(stream, 0, stream.Length, fieldName, name + GetImageExtension(mediaType))
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

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
            _ => ".png"
        };

}
