using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZyloAPI;

public partial class ZyloAPIProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Zylo API image editing is not supported.");

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ResponseFormat = "b64_json"
        }, cancellationToken);

        var outputFormat = response.OutputFormat ?? "png";
        var mediaType = outputFormat.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

        return new ImageResponse
        {
            Images = (response.Data ?? [])
                .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
                .Select(image => $"data:{mediaType};base64,{image.B64Json}")
                .ToList(),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                JsonSerializer.SerializeToElement(response)),
            Response = new HeaderResponseData
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        var response = await _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            ImageGenerationsEndpoint,
            cancellationToken);

        // Zylo is documented as OpenAI-compatible and is asked for base64, but normalize
        // URL responses as well in case an upstream image model ignores response_format.
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json) || string.IsNullOrWhiteSpace(image.Url))
                continue;

            image.B64Json = Convert.ToBase64String(
                await _client.GetByteArrayAsync(image.Url, cancellationToken));
            image.Url = null;
        }

        return response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Zylo does not expose image SSE. Adapt its synchronous response to the
        // streaming contract by emitting one completed event per generated image.
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Quality = response.Quality ?? options.Quality,
                Size = response.Size ?? options.Size,
                Usage = response.Usage
            };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Zylo API image editing is not supported.");

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Zylo API image editing is not supported.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
