using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Simplismart;

public partial class SimplismartProvider
{
    private const string SimplismartFluxEndpoint = "model/infer/flux";

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = SimplismartCreatePayload(metadata);
        payload["prompt"] = request.Prompt;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;
        if (request.N.HasValue) payload["num_images_per_prompt"] = request.N.Value;
        SimplismartApplySize(payload, request.Size);

        var result = await SimplismartPostJsonAsync(SimplismartFluxEndpoint, payload, cancellationToken);
        var images = SimplismartReadStringArray(result.Body, "images")
            .Select(image => $"data:image/png;base64,{image}")
            .ToArray();
        if (images.Length == 0)
            throw new InvalidOperationException("SimpliSmart Flux response did not contain generated images.");

        var warnings = new List<object>();
        if (request.Files?.Any() == true) warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Body),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prompt);

        var payload = options.AdditionalProperties?.ToDictionary(x => x.Key, x => (object?)x.Value.Clone())
            ?? new Dictionary<string, object?>();
        payload["prompt"] = options.Prompt;
        if (options.N.HasValue) payload["num_images_per_prompt"] = options.N.Value;
        SimplismartApplySize(payload, options.Size);

        var result = await SimplismartPostJsonAsync(SimplismartFluxEndpoint, payload, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            OutputFormat = "png",
            Size = options.Size,
            Data = SimplismartReadStringArray(result.Body, "images")
                .Select(image => new OpenAIImageData { B64Json = image })
                .ToList()
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Size = options.Size,
                    OutputFormat = "png"
                };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SimpliSmart Flux does not document image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SimpliSmart Flux does not document image editing.");

    private static void SimplismartApplySize(Dictionary<string, object?> payload, string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return;
        var parts = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            payload["width"] = width;
            payload["height"] = height;
        }
    }
}
