using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var result = IsWanImageModel(request.Model)
            ? await GenerateWanImagesAsync(request, cancellationToken)
            : await GenerateOpenAICompatibleImagesAsync(request, cancellationToken);

        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:{image.MediaType};base64,{image.Base64}"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Usage = ReadImageUsage(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CreateOpenAIPayload(options.AdditionalProperties,
            "model", "prompt", "background", "moderation", "n", "output_compression", "output_format",
            "partial_images", "quality", "response_format", "size", "stream", "style", "user");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["response_format"] = "b64_json";
        Set(payload, "background", options.Background);
        Set(payload, "moderation", options.Moderation);
        Set(payload, "n", options.N);
        Set(payload, "output_compression", options.OutputCompression);
        Set(payload, "output_format", options.OutputFormat);
        Set(payload, "quality", options.Quality);
        Set(payload, "size", options.Size);
        Set(payload, "style", options.Style);
        Set(payload, "user", options.User);

        var response = await SendJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        var images = await ReadImagesAsync(response.Root, cancellationToken);
        return ToOpenAIImagesResponse(response.Root, images, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", options.Model);
        AddFormValue(form, "prompt", options.Prompt);
        AddFormValue(form, "response_format", "b64_json");
        AddFormValue(form, "background", options.Background);
        AddFormValue(form, "input_fidelity", options.InputFidelity);
        AddFormValue(form, "moderation", options.Moderation);
        AddFormValue(form, "n", options.N);
        AddFormValue(form, "output_compression", options.OutputCompression);
        AddFormValue(form, "output_format", options.OutputFormat);
        AddFormValue(form, "quality", options.Quality);
        AddFormValue(form, "size", options.Size);
        AddFormValue(form, "user", options.User);
        foreach (var file in options.ImageFiles ?? []) AddFile(form, "image", file.OpenReadStream(), file.FileName, file.ContentType);
        foreach (var image in options.Images ?? []) AddFormValue(form, "image_url", image.ImageUrl);
        if (options.MaskFile is { } mask) AddFile(form, "mask", mask.OpenReadStream(), mask.FileName, mask.ContentType);
        AddFormValue(form, "mask_url", options.Mask?.ImageUrl);
        AddAdditionalFormValues(form, options.AdditionalProperties,
            "model", "prompt", "image", "image_url", "mask", "mask_url", "background", "input_fidelity",
            "moderation", "n", "output_compression", "output_format", "partial_images", "quality", "response_format", "size", "stream", "user");

        var response = await SendMultipartJsonAsync("v1/images/edits", form, "image edit", cancellationToken);
        var images = await ReadImagesAsync(response.Root, cancellationToken);
        return ToOpenAIImagesResponse(response.Root, images, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
    }

    private async Task<ZenlayerImagesResult> GenerateOpenAICompatibleImagesAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        if (request.Files?.Any() == true || request.Mask is not null)
        {
            using var form = new MultipartFormDataContent();
            AddFormValue(form, "model", request.Model);
            AddFormValue(form, "prompt", request.Prompt);
            AddFormValue(form, "response_format", "b64_json");
            AddFormValue(form, "n", request.N);
            AddFormValue(form, "size", request.Size);
            AddProviderFormValues(form, request.ProviderOptions, GetIdentifier(), "model", "prompt", "image", "mask", "n", "size", "response_format");
            var index = 0;
            foreach (var file in request.Files ?? [])
            {
                var decoded = DecodeData(file.Data);
                AddFile(form, "image", new MemoryStream(decoded), $"image-{index++}{ImageExtension(file.MediaType)}", file.MediaType);
            }
            if (request.Mask is { } mask)
                AddFile(form, "mask", new MemoryStream(DecodeData(mask.Data)), "mask" + ImageExtension(mask.MediaType), mask.MediaType);
            var response = await SendMultipartJsonAsync("v1/images/edits", form, "image edit", cancellationToken);
            return new ZenlayerImagesResult(response.Root, response.Headers, await ReadImagesAsync(response.Root, cancellationToken));
        }

        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(), "model", "prompt", "n", "size", "response_format", "seed", "aspectRatio");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        Set(payload, "n", request.N);
        Set(payload, "size", request.Size);
        Set(payload, "seed", request.Seed);
        Set(payload, "aspect_ratio", request.AspectRatio);
        var result = await SendJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        return new ZenlayerImagesResult(result.Root, result.Headers, await ReadImagesAsync(result.Root, cancellationToken));
    }

    private async Task<ZenlayerImagesResult> GenerateWanImagesAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(), "model", "input", "parameters", "prompt", "files", "n", "size", "seed");
        payload["model"] = request.Model;
        var content = new JsonArray();
        foreach (var file in request.Files ?? []) content.Add(new JsonObject { ["image"] = NormalizeImage(file) });
        content.Add(new JsonObject { ["text"] = request.Prompt });
        payload["input"] = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content })
        };
        var parameters = payload["parameters"] as JsonObject ?? new JsonObject();
        Set(parameters, "n", request.N);
        Set(parameters, "size", request.Size);
        Set(parameters, "seed", request.Seed);
        payload["parameters"] = parameters;
        var response = await SendJsonAsync(HttpMethod.Post, "v1/services/aigc/multimodal-generation/generation", payload, "Wan image generation", cancellationToken);
        return new ZenlayerImagesResult(response.Root, response.Headers, await ReadImagesAsync(response.Root, cancellationToken));
    }

    private async Task<List<ZenlayerImage>> ReadImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<ZenlayerImage>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
            {
                var base64 = GetString(item, "b64_json");
                if (!string.IsNullOrWhiteSpace(base64)) images.Add(new ZenlayerImage(base64, "image/png", GetString(item, "revised_prompt")));
                else await AddImageUrlAsync(images, GetString(item, "url"), GetString(item, "revised_prompt"), cancellationToken);
            }
        if (root.TryGetProperty("output", out var output)
            && output.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            foreach (var choice in choices.EnumerateArray())
                if (choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var item in content.EnumerateArray()) await AddImageUrlAsync(images, GetString(item, "image"), null, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException($"Zenlayer image response contained no usable images: {root.GetRawText()}");
        return images;
    }

    private async Task AddImageUrlAsync(List<ZenlayerImage> images, string? url, string? revisedPrompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var media = await DownloadAsync(url, cancellationToken);
        images.Add(new ZenlayerImage(Convert.ToBase64String(media.Bytes), media.MediaType.StartsWith("image/") ? media.MediaType : "image/png", revisedPrompt));
    }

    private static OpenAIImagesResponse ToOpenAIImagesResponse(
        JsonElement root, IEnumerable<ZenlayerImage> images, string? background, string? outputFormat, string? quality, string? size)
        => new()
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Usage = root.TryGetProperty("usage", out var usage) ? JsonSerializer.Deserialize<OpenAIImageUsage>(usage.GetRawText(), MediaJson) : null,
            Data = images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList()
        };

    private static ImageUsageData? ReadImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return null;
        return new ImageUsageData
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var i) ? i : null,
            OutputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var o) ? o : null,
            TotalTokens = usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t) ? t : null
        };
    }

    private static bool IsWanImageModel(string model) => model.Contains("wan2.7-image", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeImage(ImageFile file) => file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        ? file.Data : $"data:{file.MediaType};base64,{file.Data}";
    private static byte[] DecodeData(string data) => Convert.FromBase64String(data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? data[(data.IndexOf(',') + 1)..] : data);
    private static string ImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
    private static void Set(JsonObject payload, string name, object? value) { if (value is not null) payload[name] = JsonValue.Create(value); }
    private sealed record ZenlayerImage(string Base64, string MediaType, string? RevisedPrompt);
    private sealed record ZenlayerImagesResult(JsonElement Root, Dictionary<string, string> Headers, List<ZenlayerImage> Images);
}
