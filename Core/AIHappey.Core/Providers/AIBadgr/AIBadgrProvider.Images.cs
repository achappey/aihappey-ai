using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIBadgr;

public partial class AIBadgrProvider
{
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var streamEvent in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
            options,
            cancellationToken: cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var response = await OpenAIImageGenerationRequestAsync(new()
        {
            Model = NormalizeProviderModelId(request.Model),
            Prompt = request.Prompt,
            N = request.N ?? 1,
            Size = request.Size,
            ResponseFormat = "url"
        }, cancellationToken);

        var images = response.Data?
            .Select(image => !string.IsNullOrWhiteSpace(image.Url)
                ? image.Url
                : !string.IsNullOrWhiteSpace(image.B64Json)
                    ? $"data:image/png;base64,{image.B64Json}"
                    : null)
            .Where(image => image is not null)
            .Cast<string>()
            .ToList() ?? [];

        if (images.Count == 0)
            throw new InvalidOperationException("AI Badgr image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
}
