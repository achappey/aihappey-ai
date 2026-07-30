using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SudoRouter;

public partial class SudoRouterProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "SudoRouter's documented generations endpoint accepts prompt-based generation only." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "SudoRouter's documented generations endpoint does not accept a mask." });
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Use SudoRouter provider options when the selected model supports an aspect ratio field." });

        var payload = GetSudoRouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N.HasValue)
            payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;
        payload["response_format"] = "b64_json";

        var result = await SendSudoRouterJsonAsync(HttpMethod.Post, "v1/images/generations", payload, cancellationToken);
        var images = await ExtractSudoRouterImagesAsync(result.Root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("SudoRouter image generation returned no images.");

        return new ImageResponse
        {
            Images = images.Select(static image => image.DataUrl),
            Warnings = warnings,
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
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(options));

        var payload = JsonSerializer.SerializeToNode(options)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the SudoRouter image-generation request.");
        payload["response_format"] = "b64_json";
        var result = await SendSudoRouterJsonAsync(HttpMethod.Post, "v1/images/generations", payload, cancellationToken);
        return await CreateSudoRouterOpenAIImagesResponseAsync(result.Root, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // SudoRouter does not document image SSE; emit completed events after the synchronous result.
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        // The endpoint is documented by SudoRouter as OpenAI-compatible, but its exact multipart
        // field contract is not published. The shared compatibility helper preserves all OpenAI fields.
        var response = await _client.OpenAICompatibleImageEditRequestAsync(options, cancellationToken: cancellationToken);
        await NormalizeSudoRouterImageResponseAsync(response, cancellationToken);
        return response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    private async Task<OpenAIImagesResponse> CreateSudoRouterOpenAIImagesResponseAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = await ExtractSudoRouterImagesAsync(root, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var unixTime)
                ? unixTime
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images.Select(image => new OpenAIImageData
            {
                B64Json = image.Base64,
                RevisedPrompt = image.RevisedPrompt
            }).ToList()
        };
    }

    private async Task NormalizeSudoRouterImageResponseAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
    {
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                image.B64Json = NormalizeSudoRouterBase64(image.B64Json);
                continue;
            }

#pragma warning disable CS0618
            if (string.IsNullOrWhiteSpace(image.Url))
                continue;
            var binary = await DownloadSudoRouterMediaAsync(image.Url, "image/png", cancellationToken);
#pragma warning restore CS0618
            image.B64Json = Convert.ToBase64String(binary.Bytes);
        }
    }

    private async Task<List<SudoRouterImage>> ExtractSudoRouterImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<SudoRouterImage>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            var revisedPrompt = item.TryGetProperty("revised_prompt", out var revised) && revised.ValueKind == JsonValueKind.String
                ? revised.GetString()
                : null;
            if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                var base64 = NormalizeSudoRouterBase64(b64.GetString()!);
                images.Add(new SudoRouterImage(base64, $"data:image/png;base64,{base64}", revisedPrompt));
                continue;
            }

            if (!item.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString()))
                continue;

            var binary = await DownloadSudoRouterMediaAsync(url.GetString()!, "image/png", cancellationToken);
            images.Add(new SudoRouterImage(Convert.ToBase64String(binary.Bytes), ToSudoRouterDataUrl(binary.Bytes, binary.MediaType), revisedPrompt));
        }

        return images;
    }

    private sealed record SudoRouterImage(string Base64, string DataUrl, string? RevisedPrompt);



}
