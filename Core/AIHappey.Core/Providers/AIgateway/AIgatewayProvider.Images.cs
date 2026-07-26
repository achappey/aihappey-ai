using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await GenerateImagesAsync(CreateAIgatewayPayload(new()
        {
            ["model"] = request.Model, ["prompt"] = request.Prompt, ["n"] = request.N,
            ["size"] = request.Size, ["response_format"] = "b64_json"
        }, request.ProviderOptions, "model", "prompt", "n", "size", "response_format"), "v1/images/generations", cancellationToken);

        return new ImageResponse
        {
            Images = response.Images.Select(image => $"data:{image.MimeType};base64,{image.Base64}").ToList(),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response.Root),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = response.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await GenerateImagesAsync(new Dictionary<string, object?>
        {
            ["model"] = options.Model, ["prompt"] = options.Prompt, ["n"] = options.N, ["size"] = options.Size,
            ["quality"] = options.Quality, ["style"] = options.Style, ["response_format"] = "b64_json"
        }, "v1/images/generations", cancellationToken);
        return ToOpenAIImagesResponse(response, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent(options.Prompt), "prompt");
        form.Add(new StringContent("b64_json"), "response_format");
        if (options.N is not null) form.Add(new StringContent(options.N.Value.ToString()), "n");
        if (!string.IsNullOrWhiteSpace(options.Size)) form.Add(new StringContent(options.Size), "size");
        foreach (var file in options.ImageFiles ?? [])
        {
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "image", file.FileName);
        }
        if (options.Images?.FirstOrDefault()?.ImageUrl is { } imageUrl)
            form.Add(new StringContent(imageUrl), "image_url");
        if (options.MaskFile is { } mask)
        {
            var content = new StreamContent(mask.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(mask.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "mask", mask.FileName);
        }
        using var httpResponse = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        var root = await ReadAIgatewayJsonAsync(httpResponse, "image edit", cancellationToken);
        var generated = await ReadAIgatewayImagesAsync(root, cancellationToken);
        return ToOpenAIImagesResponse(generated, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    private async Task<AIgatewayImagesResult> GenerateImagesAsync(object payload, string endpoint, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateAIgatewayJsonRequest(HttpMethod.Post, endpoint, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadAIgatewayJsonAsync(response, "image generation", cancellationToken);
        var result = await ReadAIgatewayImagesAsync(root, cancellationToken);
        return result with { Headers = response.GetHeaders() };
    }

    private async Task<AIgatewayImagesResult> ReadAIgatewayImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AIgateway image response did not contain a data array.");
        var images = new List<AIgatewayImage>();
        foreach (var image in data.EnumerateArray())
            images.Add(new AIgatewayImage(await ResolveAIgatewayImageBase64Async(image, cancellationToken), "image/png", GetAIgatewayString(image, "revised_prompt")));
        return new AIgatewayImagesResult(root, [], images);
    }

    private static OpenAIImagesResponse ToOpenAIImagesResponse(AIgatewayImagesResult result, string? background, string? outputFormat, string? quality, string? size)
        => new()
        {
            Created = result.Root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background, OutputFormat = outputFormat, Quality = quality, Size = size,
            Data = result.Images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList()
        };

    private sealed record AIgatewayImage(string Base64, string MimeType, string? RevisedPrompt);
    private sealed record AIgatewayImagesResult(JsonElement Root, Dictionary<string, string> Headers, List<AIgatewayImage> Images);
}
