using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        var payload = CreateEmpirioVercelPayload(request.ProviderOptions,
            "model", "prompt", "image", "mask", "num_images", "n", "aspect_ratio", "aspectRatio", "resolution", "size", "response_format", "sync", "wait");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["sync"] = true;
        payload["response_format"] = "b64_json";
        SetEmpirio(payload, "num_images", request.N);
        SetEmpirio(payload, "resolution", request.Size);
        SetEmpirio(payload, "aspect_ratio", request.AspectRatio);
        SetEmpirio(payload, "seed", request.Seed);
        var images = new JsonArray();
        foreach (var file in request.Files ?? []) images.Add(EmpirioImageValue(file));
        if (images.Count > 0) payload["image"] = images;
        if (request.Mask is not null) payload["mask"] = EmpirioImageValue(request.Mask);

        var result = await SendEmpirioImageRequestAsync(payload, cancellationToken);
        var resolved = await ReadEmpirioImagesAsync(result.Root, cancellationToken);
        return new ImageResponse
        {
            Images = resolved.Select(image => $"data:{image.MediaType};base64,{image.Base64}"),
            Warnings = [],
            Usage = ReadEmpirioImageUsage(result.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
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
        var payload = CreateEmpirioOpenAIPayload(options.AdditionalProperties,
            "model", "prompt", "n", "num_images", "size", "resolution", "response_format", "sync", "wait", "stream");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["sync"] = true;
        payload["response_format"] = "b64_json";
        SetEmpirio(payload, "num_images", options.N);
        SetEmpirio(payload, "resolution", options.Size);
        SetEmpirio(payload, "background", options.Background);
        SetEmpirio(payload, "moderation", options.Moderation);
        SetEmpirio(payload, "output_format", options.OutputFormat);
        SetEmpirio(payload, "quality", options.Quality);
        SetEmpirio(payload, "style", options.Style);
        SetEmpirio(payload, "user", options.User);
        var result = await SendEmpirioImageRequestAsync(payload, cancellationToken);
        return await ToEmpirioOpenAIImagesAsync(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
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
        var payload = CreateEmpirioOpenAIPayload(options.AdditionalProperties,
            "model", "prompt", "image", "images", "mask", "n", "num_images", "size", "resolution", "response_format", "sync", "wait", "stream");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["sync"] = true;
        payload["response_format"] = "b64_json";
        SetEmpirio(payload, "num_images", options.N);
        SetEmpirio(payload, "resolution", options.Size);
        SetEmpirio(payload, "background", options.Background);
        SetEmpirio(payload, "input_fidelity", options.InputFidelity);
        SetEmpirio(payload, "moderation", options.Moderation);
        SetEmpirio(payload, "output_format", options.OutputFormat);
        SetEmpirio(payload, "quality", options.Quality);
        SetEmpirio(payload, "user", options.User);
        var images = new JsonArray();
        foreach (var image in options.Images ?? [])
            if (!string.IsNullOrWhiteSpace(image.ImageUrl)) images.Add(image.ImageUrl);
        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            images.Add($"data:{file.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}");
        }
        if (images.Count > 0) payload["image"] = images;
        if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl)) payload["mask"] = options.Mask.ImageUrl;
        if (options.MaskFile is { } mask)
        {
            await using var stream = mask.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            payload["mask"] = $"data:{mask.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}";
        }
        var result = await SendEmpirioImageRequestAsync(payload, cancellationToken);
        return await ToEmpirioOpenAIImagesAsync(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
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

    private async Task<EmpirioJsonResult> SendEmpirioImageRequestAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var submitted = await SendEmpirioJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        return await AwaitEmpirioJobAsync(submitted, "image generation", cancellationToken);
    }

    private async Task<List<EmpirioImage>> ReadEmpirioImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        CollectEmpirioImageItems(root, values);
        var payloadRoot = GetEmpirioPayloadRoot(root);
        if (payloadRoot.GetRawText() != root.GetRawText()) CollectEmpirioImageItems(payloadRoot, values);
        var images = new List<EmpirioImage>();
        foreach (var item in values)
        {
            var base64 = GetEmpirioString(item, "b64_json");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                images.Add(new EmpirioImage(base64, "image/png", GetEmpirioString(item, "revised_prompt")));
                continue;
            }
            var url = item.ValueKind == JsonValueKind.String ? item.GetString() : GetEmpirioString(item, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var downloaded = await DownloadEmpirioMediaAsync(url, "image/png", cancellationToken);
            images.Add(new EmpirioImage(Convert.ToBase64String(downloaded.Bytes), downloaded.MediaType, GetEmpirioString(item, "revised_prompt")));
        }
        if (images.Count == 0) throw new InvalidOperationException($"EmpirioLabs image response contained no usable images: {root.GetRawText()}");
        return images;
    }

    private static void CollectEmpirioImageItems(JsonElement root, List<JsonElement> values)
    {
        foreach (var name in new[] { "data", "images", "output" })
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                values.AddRange(array.EnumerateArray().Select(item => item.Clone()));
    }

    private async Task<OpenAIImagesResponse> ToEmpirioOpenAIImagesAsync(
        JsonElement root, string? background, string? outputFormat, string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ReadEmpirioImagesAsync(root, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp)
                ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Usage = ReadEmpirioOpenAIImageUsage(root),
            Data = images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList()
        };
    }

    private static ImageUsageData? ReadEmpirioImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new ImageUsageData
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var i) ? i : null,
            OutputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var o) ? o : null,
            TotalTokens = usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t) ? t : null
        };
    }

    private static OpenAIImageUsage? ReadEmpirioOpenAIImageUsage(JsonElement root)
        => root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<OpenAIImageUsage>(usage.GetRawText(), EmpirioMediaJson) : null;

    private static string EmpirioImageValue(ImageFile file)
        => file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data : $"data:{file.MediaType};base64,{file.Data}";

    private sealed record EmpirioImage(string Base64, string MediaType, string? RevisedPrompt);
}
