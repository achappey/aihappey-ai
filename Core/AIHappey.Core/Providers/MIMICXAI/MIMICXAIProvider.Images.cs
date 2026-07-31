using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await GenerateImageAsync(request.Model, request.Prompt, cancellationToken);
        var image = RequireBase64(result.Root, "image", "image_b64");
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Size is not null || request.AspectRatio is not null || request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "size/aspectRatio/seed" });
        if (request.Files?.Any() == true || request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "image editing" });
        return new ImageResponse
        {
            Images = [$"data:image/png;base64,{image}"], Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Prompt)) throw new ArgumentException("'prompt' is required.", nameof(options));
        var result = await GenerateImageAsync(options.Model, options.Prompt, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Background = options.Background,
            OutputFormat = options.OutputFormat ?? "png", Quality = options.Quality, Size = options.Size,
            Data = [new OpenAIImageData { B64Json = RequireBase64(result.Root, "image", "image_b64") }]
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MIMICXAI does not document an image-edit input contract for this endpoint.");

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("MIMICXAI does not document an image-edit input contract for this endpoint.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private Task<MimicXJsonResult> GenerateImageAsync(string? model, string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.", nameof(prompt));
        if (NormalizeAgentModel(model) != "Nova") throw new NotSupportedException("MIMICXAI media generation requires Nova.");
        return PostJsonAsync(AgentEndpoint, new { model = "Nova", prompt, stream = false }, "image generation", cancellationToken);
    }
}
