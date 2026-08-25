using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Router9;

public partial class Router9Provider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRouter9ImageRequest(request.Model, request.Prompt);
        var payload = CreateRouter9Payload(request.ProviderOptions, "model", "prompt", "n", "size", "aspect_ratio", "seed", "images");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N is not null) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var images = await ToRouter9VercelImagesAsync(request.Files, cancellationToken);
        if (images.Count > 0) payload["images"] = images;
        if (request.Mask is not null) throw new NotSupportedException("Router9 image generation does not support masks.");
        var result = await GenerateRouter9ImagesAsync(payload, cancellationToken);
        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:{image.MediaType};base64,{image.Base64}"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
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
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CreateRouter9ImagePayload(options.AdditionalProperties, options.Model, options.Prompt, options.N,
            options.Background, options.OutputCompression, options.OutputFormat, options.Quality, options.Size);
        var result = await GenerateRouter9ImagesAsync(payload, cancellationToken);
        return ToRouter9OpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background,
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("Router9 image editing does not support masks.");
        var payload = CreateRouter9ImagePayload(options.AdditionalProperties, options.Model, options.Prompt, options.N,
            options.Background, options.OutputCompression, options.OutputFormat, options.Quality, options.Size);
        var images = await ToRouter9OpenAIImagesAsync(options, cancellationToken);
        if (images.Count == 0) throw new ArgumentException("At least one image is required.", nameof(options));
        payload["images"] = images;
        var result = await GenerateRouter9ImagesAsync(payload, cancellationToken);
        return ToRouter9OpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background,
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size
            };
        }
    }

    private async Task<Router9ImagesResult> GenerateRouter9ImagesAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var result = await SendRouter9JsonAsync("v1/image/generations", payload, "image generation", cancellationToken);
        if (!result.Root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Router9 image response did not contain data.");
        var images = data.EnumerateArray().Select(item => new Router9Image(
            GetRouter9String(item, "b64_json") ?? throw new InvalidOperationException("Router9 image response contained no b64_json."),
            GetRouter9String(item, "media_type") ?? "image/webp",
            GetRouter9String(item, "revised_prompt"))).ToList();
        if (images.Count == 0) throw new InvalidOperationException("Router9 image response contained no images.");
        return new Router9ImagesResult(result.Root, result.Headers, images);
    }

    private static JsonObject CreateRouter9ImagePayload(Dictionary<string, JsonElement>? additional, string model, string prompt,
        int? n, string? background, int? compression, string? format, string? quality, string? size)
    {
        ValidateRouter9ImageRequest(model, prompt);
        var payload = CreateRouter9Payload(additional, "model", "prompt", "n", "background", "output_compression", "output_format", "quality", "size", "images");
        payload["model"] = model; payload["prompt"] = prompt;
        if (n is not null) payload["n"] = n.Value;
        if (!string.IsNullOrWhiteSpace(background)) payload["background"] = background;
        if (compression is not null) payload["output_compression"] = compression.Value;
        if (!string.IsNullOrWhiteSpace(format)) payload["output_format"] = format;
        if (!string.IsNullOrWhiteSpace(quality)) payload["quality"] = quality;
        if (!string.IsNullOrWhiteSpace(size)) payload["size"] = size;
        return payload;
    }

    private static void ValidateRouter9ImageRequest(string model, string prompt)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.", nameof(prompt));
    }

    private static async Task<JsonArray> ToRouter9OpenAIImagesAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        var images = new JsonArray();
        foreach (var reference in options.Images ?? []) if (!string.IsNullOrWhiteSpace(reference.ImageUrl)) images.Add(reference.ImageUrl);
        foreach (var file in options.ImageFiles ?? [])
        {
            await using var input = file.OpenReadStream(); using var memory = new MemoryStream();
            await input.CopyToAsync(memory, cancellationToken);
            images.Add($"data:{file.ContentType ?? "image/png"};base64,{Convert.ToBase64String(memory.ToArray())}");
        }
        return images;
    }

    private static Task<JsonArray> ToRouter9VercelImagesAsync(IEnumerable<ImageFile>? files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var images = new JsonArray();
        foreach (var file in files ?? []) images.Add(file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data : $"data:{file.MediaType};base64,{file.Data}");
        return Task.FromResult(images);
    }

    private static OpenAIImagesResponse ToRouter9OpenAIImagesResponse(Router9ImagesResult result, string? background, string? format, string? quality, string? size)
        => new() { Created = result.Root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background, OutputFormat = format, Quality = quality, Size = size,
            Data = result.Images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList() };

    private sealed record Router9Image(string Base64, string MediaType, string? RevisedPrompt);
    private sealed record Router9ImagesResult(JsonElement Root, Dictionary<string, string> Headers, List<Router9Image> Images);
}
