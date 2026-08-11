using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Net.Mime;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(
            ToOpenRouterImageGenerationRequest(options),
            endpoint: "v1/images",
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationStreamingAsync(
            ToOpenRouterImageGenerationRequest(options),
            endpoint: "v1/images",
            cancellationToken);
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var translated = await ToOpenRouterImageEditRequestAsync(options, cancellationToken);
        return await _client.OpenAICompatibleImageGenerationRequestAsync(
            translated,
            endpoint: "v1/images",
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var translated = await ToOpenRouterImageEditRequestAsync(options, cancellationToken);

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationStreamingAsync(
            translated,
            endpoint: "v1/images",
            cancellationToken))
        {
            yield return streamEvent switch
            {
                OpenAIImageGenerationPartialImage partial => new OpenAIImageEditPartialImage
                {
                    B64Json = partial.B64Json,
                    CreatedAt = partial.CreatedAt,
                    PartialImageIndex = partial.PartialImageIndex,
                    Size = partial.Size,
                    Quality = partial.Quality,
                    Background = partial.Background,
                    OutputFormat = partial.OutputFormat
                },
                OpenAIImageGenerationCompleted completed => new OpenAIImageEditCompleted
                {
                    B64Json = completed.B64Json,
                    CreatedAt = completed.CreatedAt,
                    Size = completed.Size,
                    Quality = completed.Quality,
                    Background = completed.Background,
                    OutputFormat = completed.OutputFormat,
                    Usage = completed.Usage
                },
                _ => streamEvent
            };
        }
    }

    private static OpenAIImageGenerationRequest ToOpenRouterImageGenerationRequest(OpenAIImageGenerationRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new OpenAIImageGenerationRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            Background = options.Background,
            N = options.N,
            OutputCompression = options.OutputCompression,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size,
            Stream = options.Stream,
            AdditionalProperties = CloneOpenRouterImageAdditionalProperties(options.AdditionalProperties)
        };
    }

    private static async Task<OpenAIImageGenerationRequest> ToOpenRouterImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var additionalProperties = CloneOpenRouterImageAdditionalProperties(options.AdditionalProperties) ?? [];
        var references = new List<object>();

        foreach (var image in options.Images ?? [])
        {
            var url = image.ImageUrl ?? image.FileId;
            if (!string.IsNullOrWhiteSpace(url))
                references.Add(ToOpenRouterImageReference(url));
        }

        foreach (var imageFile in options.ImageFiles ?? [])
            references.Add(ToOpenRouterImageReference(await ToOpenRouterImageDataUrlAsync(imageFile, cancellationToken)));

        if (references.Count > 0)
            additionalProperties["input_references"] = JsonSerializer.SerializeToElement(references, OpenRouterImageJsonOptions);

        var maskUrl = options.Mask?.ImageUrl ?? options.Mask?.FileId;
        if (options.MaskFile is not null)
            maskUrl = await ToOpenRouterImageDataUrlAsync(options.MaskFile, cancellationToken);

        if (!string.IsNullOrWhiteSpace(maskUrl))
            additionalProperties["mask"] = JsonSerializer.SerializeToElement(ToOpenRouterImageReference(maskUrl));

        if (!string.IsNullOrWhiteSpace(options.InputFidelity))
            additionalProperties["input_fidelity"] = JsonSerializer.SerializeToElement(options.InputFidelity);

        return new OpenAIImageGenerationRequest
        {
            Model = options.Model,
            Prompt = options.Prompt,
            Background = options.Background,
            N = options.N,
            OutputCompression = options.OutputCompression,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size,
            Stream = options.Stream,
            AdditionalProperties = additionalProperties
        };
    }

    private static Dictionary<string, JsonElement>? CloneOpenRouterImageAdditionalProperties(
        Dictionary<string, JsonElement>? additionalProperties)
        => additionalProperties?.ToDictionary(property => property.Key, property => property.Value.Clone(), StringComparer.Ordinal);

    private static object ToOpenRouterImageReference(string url) => new
    {
        type = "image_url",
        image_url = new { url }
    };

    private static async Task<string> ToOpenRouterImageDataUrlAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken);

        var mediaType = string.IsNullOrWhiteSpace(file.ContentType)
            ? MediaTypeNames.Image.Png
            : file.ContentType;
        return Convert.ToBase64String(output.ToArray()).ToDataUrl(mediaType);
    }
}
