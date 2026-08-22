using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true) warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Seed.HasValue) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = request.Model, Prompt = request.Prompt, N = request.N,
            Size = request.Size, ResponseFormat = "b64_json"
        }, cancellationToken);
        var images = response.Data?.Select(x => !string.IsNullOrWhiteSpace(x.B64Json)
            ? $"data:image/png;base64,{x.B64Json}" : x.Url).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() ?? [];
        if (images.Count == 0) throw new InvalidOperationException("Vultr image response contained no images.");

        return new ImageResponse
        {
            Images = images, Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new() { Timestamp = response.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAIImageGenerationRequest();
     
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
       
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(options, "images/generations", cancellationToken))
            yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

}
