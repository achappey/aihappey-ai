using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var payload = CreateRunPayload(request.ProviderOptions, request.Model);
        if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;

        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 0)
        {
            var urls = files.Select(file => RequireMediaReference(file.Data, "files")).ToArray();
            // A model may use image, images, image_url, or a nested structure.
            // Clients can choose the exact field in providerOptions.sandbase.
            if (!payload.ContainsKey("image") && !payload.ContainsKey("images"))
                payload[files.Length == 1 ? "image" : "images"] = files.Length == 1 ? urls[0] : urls;
        }
        if (request.Mask is not null)
        {
            var mask = RequireMediaReference(request.Mask.Data, "mask");
            if (!payload.ContainsKey("mask")) payload["mask"] = mask;
        }

        var run = await AwaitRunAsync(await SubmitRunAsync(payload, cancellationToken), cancellationToken);
        var images = new List<string>();
        foreach (var output in RunOutputs(run.Root))
        {
            var url = FindMedia(output, "image");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var (bytes, mime) = await ReadRunMediaAsync(url, "image/png", cancellationToken);
            images.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
        }
        if (images.Count == 0) throw new InvalidOperationException("SandBase completed the image run without a usable image output.");
        return new ImageResponse
        {
            Images = images, ProviderMetadata = RunMetadata(run.Root),
            Response = new() { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Headers = run.Headers }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        var extras = options.AdditionalProperties?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Clone()) ?? [];
        if (options.OutputFormat is not null) extras["output_format"] = options.OutputFormat;
        if (options.Quality is not null) extras["quality"] = options.Quality;
        if (options.Background is not null) extras["background"] = options.Background;
        if (extras.Count != 0) request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(extras) };
        return (await ImageRequest(request, cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        var response = await ImageRequest(request, cancellationToken);
        foreach (var item in response.ToOpenAIImageGenerationCompletedEvents(options)) yield return item;
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        var request = CreateImageEditRequest(options);
        return (await ImageRequest(request, cancellationToken)).ToOpenAIImagesResponse(options);
    }

    private ImageRequest CreateImageEditRequest(OpenAIImageEditRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ImageFiles?.Length > 0 || options.MaskFile is not null)
            throw new NotSupportedException("SandBase image edits require public URL references; multipart/base64 uploads are not supported.");
        var images = options.Images?.Select(image => new ImageFile { Type = "url", Data = RequireMediaReference(image.ImageUrl, "images"), MediaType = "image/png" }).ToArray();
        var request = new ImageRequest { Model = options.Model, Prompt = options.Prompt, Files = images, N = options.N, Size = options.Size };
        if (options.Mask is not null) request.Mask = new ImageFile { Type = "url", Data = RequireMediaReference(options.Mask.ImageUrl, "mask"), MediaType = "image/png" };
        var extras = options.AdditionalProperties?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Clone()) ?? [];
        if (options.OutputFormat is not null) extras["output_format"] = options.OutputFormat;
        if (options.Quality is not null) extras["quality"] = options.Quality;
        if (options.InputFidelity is not null) extras["input_fidelity"] = options.InputFidelity;
        if (options.Background is not null) extras["background"] = options.Background;
        if (extras.Count != 0) request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(extras) };
        return request;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await ImageRequest(CreateImageEditRequest(options), cancellationToken);
        foreach (var item in result.ToOpenAIImageEditCompletedEvents(options)) yield return item;
    }
}
