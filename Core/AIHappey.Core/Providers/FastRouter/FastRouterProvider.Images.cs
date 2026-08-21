using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FastRouter;

public partial class FastRouterProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = CreateFastRouterPayload(request.ProviderOptions,
            "model", "prompt", "n", "size", "response_format", "aspectRatio", "aspect_ratio", "seed");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        if (request.N is not null) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspectRatio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;

        var result = await GenerateFastRouterImagesAsync(payload, cancellationToken);
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
        var payload = CreateFastRouterPayload(options.AdditionalProperties,
            "model", "prompt", "background", "moderation", "n", "output_compression", "output_format",
            "partial_images", "quality", "response_format", "size", "stream", "style", "user");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["response_format"] = "b64_json";
        AddFastRouterImageOption(payload, "background", options.Background);
        AddFastRouterImageOption(payload, "n", options.N);
        AddFastRouterImageOption(payload, "output_compression", options.OutputCompression);
        AddFastRouterImageOption(payload, "output_format", options.OutputFormat);
        AddFastRouterImageOption(payload, "quality", options.Quality);
        AddFastRouterImageOption(payload, "size", options.Size);
        AddFastRouterImageOption(payload, "style", options.Style);
        AddFastRouterImageOption(payload, "user", options.User);

        var result = await GenerateFastRouterImagesAsync(payload, cancellationToken);
        return ToFastRouterOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
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
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        AddFastRouterFormValue(form, "model", options.Model);
        AddFastRouterFormValue(form, "prompt", options.Prompt);
        AddFastRouterFormValue(form, "response_format", "b64_json");
        AddFastRouterFormValue(form, "background", options.Background);
        AddFastRouterFormValue(form, "n", options.N);
        AddFastRouterFormValue(form, "output_compression", options.OutputCompression);
        AddFastRouterFormValue(form, "output_format", options.OutputFormat);
        AddFastRouterFormValue(form, "quality", options.Quality);
        AddFastRouterFormValue(form, "size", options.Size);
        AddFastRouterFormValue(form, "user", options.User);

        foreach (var file in options.ImageFiles ?? [])
        {
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "image", file.FileName);
        }

        foreach (var image in options.Images ?? [])
            AddFastRouterFormValue(form, "image_url", image.ImageUrl);

        if (options.MaskFile is { } mask)
        {
            var content = new StreamContent(mask.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(mask.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "mask", mask.FileName);
        }
        AddFastRouterFormValue(form, "mask_url", options.Mask?.ImageUrl);
        AddFastRouterAdditionalFormValues(form, options.AdditionalProperties,
            "model", "prompt", "image", "image_url", "mask", "mask_url", "background", "n",
            "output_compression", "output_format", "quality", "response_format", "size", "stream", "user");

        using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        var json = await ReadFastRouterJsonAsync(response, "image edit", cancellationToken);
        var result = await ReadFastRouterImagesAsync(json, cancellationToken);
        return ToFastRouterOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    private async Task<FastRouterImagesResult> GenerateFastRouterImagesAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var result = await SendFastRouterJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        var taskId = GetFastRouterString(result.Root, "taskId") ?? GetFastRouterString(result.Root, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            return await ReadFastRouterImagesAsync(result, cancellationToken);

        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            ApplyAuthHeader();
            using var response = await _client.GetAsync($"v1/images/{Uri.EscapeDataString(taskId)}", cancellationToken);
            var polled = await ReadFastRouterJsonAsync(response, "image status", cancellationToken);
            if (HasFastRouterImageData(polled.Root))
                return await ReadFastRouterImagesAsync(polled, cancellationToken);

            var status = GetFastRouterString(polled.Root, "fastrouter_assets", "status")
                ?? GetFastRouterString(polled.Root, "status");
            if (status is not null && status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"FastRouter image task '{taskId}' failed: {polled.Root.GetRawText()}");
        }

        throw new TimeoutException($"FastRouter image task '{taskId}' did not complete in time.");
    }

    private async Task<FastRouterImagesResult> ReadFastRouterImagesAsync(FastRouterJsonResult result, CancellationToken cancellationToken)
    {
        var images = new List<FastRouterImage>();
        if (result.Root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                await AddFastRouterImageAsync(images, item, cancellationToken);
        }

        if (images.Count == 0 && result.Root.TryGetProperty("fastrouter_assets", out var assets)
            && assets.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in urls.EnumerateArray())
                if (url.ValueKind == JsonValueKind.String)
                    await AddFastRouterImageUrlAsync(images, url.GetString(), null, cancellationToken);
        }

        if (images.Count == 0)
            throw new InvalidOperationException("FastRouter image response did not contain any usable images.");
        return new FastRouterImagesResult(result.Root, result.Headers, images);
    }

    private async Task AddFastRouterImageAsync(List<FastRouterImage> images, JsonElement item, CancellationToken cancellationToken)
    {
        var revisedPrompt = GetFastRouterString(item, "revised_prompt");
        var base64 = GetFastRouterString(item, "b64_json");
        if (!string.IsNullOrWhiteSpace(base64))
        {
            images.Add(new FastRouterImage(base64, "image/png", revisedPrompt));
            return;
        }
        await AddFastRouterImageUrlAsync(images, GetFastRouterString(item, "url"), revisedPrompt, cancellationToken);
    }

    private async Task AddFastRouterImageUrlAsync(List<FastRouterImage> images, string? url, string? revisedPrompt, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        using var response = await _client.GetAsync(uri, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FastRouter image download failed ({(int)response.StatusCode}).");
        images.Add(new FastRouterImage(Convert.ToBase64String(bytes), response.Content.Headers.ContentType?.MediaType ?? "image/png", revisedPrompt));
    }

    private static bool HasFastRouterImageData(JsonElement root)
        => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0;

    private static void AddFastRouterImageOption(JsonObject payload, string name, object? value)
    {
        if (value is not null) payload[name] = JsonValue.Create(value);
    }

    private static OpenAIImagesResponse ToFastRouterOpenAIImagesResponse(
        FastRouterImagesResult result, string? background, string? outputFormat, string? quality, string? size)
        => new()
        {
            Created = result.Root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp)
                ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Data = result.Images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList()
        };

    private sealed record FastRouterImage(string Base64, string MediaType, string? RevisedPrompt);
    private sealed record FastRouterImagesResult(JsonElement Root, Dictionary<string, string> Headers, List<FastRouterImage> Images);
}
