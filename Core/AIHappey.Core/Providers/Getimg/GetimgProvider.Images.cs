using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Models;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Getimg;

public partial class GetimgProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var model = NormalizeGetimgModel(request.Model);
        var warnings = new List<object>();
        var payload = CreateGetimgPayload(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Size)) warnings.Add(new { type = "unsupported", feature = "size" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });

        var files = request.Files?.Where(x => x is not null).ToList() ?? [];
        if (files.Count > 0 && !payload.ContainsKey("images"))
            payload["images"] = files.Select(file => new { url = ToGetimgInputUrl(file) }).ToArray();

        var root = await SendGetimgAsync(HttpMethod.Post, "images/generations", payload, cancellationToken);
        if (string.Equals(GetGetimgString(root, "status"), "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"getimg image generation failed: {GetGetimgError(root)}");

        var images = new List<string>();
        foreach (var output in GetGetimgOutputs(root))
        {
            var mediaType = GetGetimgString(output, "mime_type");
            var b64 = GetGetimgString(output, "b64_json");
            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(b64.ToDataUrl(mediaType ?? MediaTypeNames.Image.Jpeg));
                continue;
            }

            var url = GetGetimgString(output, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var (bytes, resolvedMediaType) = await DownloadGetimgOutputAsync(url, mediaType, MediaTypeNames.Image.Jpeg, cancellationToken);
            images.Add($"data:{resolvedMediaType};base64,{Convert.ToBase64String(bytes)}");
        }

        if (images.Count == 0)
            throw new InvalidOperationException("getimg image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root, GetGetimgCost(root)),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var item in result.ToOpenAIImageGenerationCompletedEvents(options)) yield return item;
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        return result.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var result = await ImageRequest(request, cancellationToken);
        foreach (var item in result.ToOpenAIImageEditCompletedEvents(options)) yield return item;
    }
}
