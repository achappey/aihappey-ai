using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Models;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.PrunaAI;

public partial class PrunaAIProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        var input = CreatePrunaInput(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        input["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) input["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) input["seed"] = request.Seed.Value;
        if (request.N is not null) input["num_outputs"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) warnings.Add(new { type = "unsupported", feature = "size" });

        var files = request.Files?.Where(x => x is not null).ToList() ?? [];
        if (request.Mask is not null) files.Add(request.Mask);
        if (files.Count > 0)
        {
            var images = new List<string>();
            foreach (var file in files)
                images.Add(await UploadPrunaFileAsync(file.Data, file.MediaType, cancellationToken));
            input["images"] = images;
        }

        var root = await SendPrunaPredictionAsync(request.Model, input, true, cancellationToken);
        var status = GetPrunaString(root, "status");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Pruna image prediction failed: {GetPrunaError(root)}");

        var url = GetPrunaString(root, "generation_url", "output_url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Pruna image prediction returned status '{status}' without a generation URL.");

        var (bytes, mediaType) = await DownloadPrunaOutputAsync(url, MediaTypeNames.Image.Jpeg, cancellationToken);
        return new ImageResponse
        {
            Images = [$"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
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
