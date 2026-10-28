using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider
{
    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("RouterPlus documents image generation, not image editing.");
        if (request.Seed is not null || request.AspectRatio is not null)
            throw new NotSupportedException("RouterPlus image generation does not document seed or aspectRatio.");
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && (providerOptions.ValueKind != JsonValueKind.Object || providerOptions.EnumerateObject().Any()))
            throw new ArgumentException("RouterPlus image generation does not document provider-specific options.", nameof(request));

        ApplyAuthHeader();
        var response = await _client.OpenAICompatibleImageGenerationRequestAsync(
            new OpenAIImageGenerationRequest
            {
                Model = NormalizeModelId(request.Model),
                Prompt = request.Prompt,
                N = request.N,
                Size = request.Size
            },
            endpoint: "v1/images/generations",
            cancellationToken);
        var format = response.OutputFormat ?? "png";
        var images = (response.Data ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.B64Json))
            .Select(item => $"data:image/{format};base64,{item.B64Json}")
            .ToList();
        if (images.Count == 0)
            throw new InvalidOperationException("RouterPlus image generation returned no base64 images.");

        return new ImageResponse
        {
            Images = images,
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web)),
            Response = new HeaderResponseData
            {
                Timestamp = response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime
                    : DateTime.UtcNow,
                ModelId = NormalizeModelId(request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ApplyAuthHeader();
        options.Model = NormalizeModelId(options.Model);
        return await _client.OpenAICompatibleImageGenerationRequestAsync(
            options, endpoint: "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ApplyAuthHeader();
        options.Model = NormalizeModelId(options.Model);
        await foreach (var part in _client.OpenAICompatibleImageGenerationStreamingAsync(
            options, endpoint: "v1/images/generations", cancellationToken))
            yield return part;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("RouterPlus documents image generation, not image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("RouterPlus documents image generation, not image editing.");
}
