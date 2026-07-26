using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NEARAI;

public partial class NEARAIProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = NEARAIJsonObject(request.ProviderOptions, "model", "prompt", "n", "size", "response_format");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        payload["response_format"] = "b64_json";
        var result = await NEARAIGenerateImagesAsync(payload, "v1/images/generations", cancellationToken);
        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:image/png;base64,{image.Base64}").ToList(),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => NEARAIToOpenAIImagesAsync(ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken), options.Background, options.OutputFormat, options.Quality, options.Size);

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        return NEARAIImageGenerationStream(options, cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return NEARAIEditImagesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        return NEARAIImageEditStream(options, cancellationToken);
    }

    private async IAsyncEnumerable<IOpenAIImageStreamEvent> NEARAIImageGenerationStream(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    private async IAsyncEnumerable<IOpenAIImageStreamEvent> NEARAIImageEditStream(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageEditCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    private async Task<OpenAIImagesResponse> NEARAIEditImagesAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent(options.Prompt), "prompt");
        form.Add(new StringContent("b64_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(options.Size)) form.Add(new StringContent(options.Size), "size");
        foreach (var file in options.ImageFiles ?? [])
        {
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "image", file.FileName);
        }
        foreach (var property in options.AdditionalProperties ?? [])
            if (!new[] { "model", "prompt", "image", "response_format", "size" }.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                form.Add(new StringContent(property.Value.GetRawText()), property.Key);
        using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NEARAI image edit request failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return await NEARAIToOpenAIImagesResponseAsync(document.RootElement.Clone(), options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    private async Task<NEARAIImageResult> NEARAIGenerateImagesAsync(object payload, string endpoint, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(payload, NEARAIJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NEARAI image generation request failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        return new NEARAIImageResult(root, response.GetHeaders(), await NEARAIReadImagesAsync(root, cancellationToken));
    }

    private async Task<OpenAIImagesResponse> NEARAIToOpenAIImagesAsync(Task<ImageResponse> responseTask, string? background, string? outputFormat, string? quality, string? size)
    {
        var response = await responseTask;
        return new OpenAIImagesResponse { Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Background = background, OutputFormat = outputFormat, Quality = quality, Size = size, Data = response.Images?.Select(image => new OpenAIImageData { B64Json = image[(image.IndexOf(',') + 1)..] }).ToList() };
    }

    private async Task<OpenAIImagesResponse> NEARAIToOpenAIImagesResponseAsync(JsonElement root, string? background, string? outputFormat, string? quality, string? size, CancellationToken cancellationToken)
        => new() { Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp) ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Background = background, OutputFormat = outputFormat, Quality = quality, Size = size, Data = (await NEARAIReadImagesAsync(root, cancellationToken)).Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList() };

    private async Task<List<NEARAIImage>> NEARAIReadImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<NEARAIImage>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("NEARAI image response did not contain a data array.");
        foreach (var item in data.EnumerateArray())
        {
            var value = item.TryGetProperty("b64_json", out var base64) ? base64.GetString() : null;
            if (string.IsNullOrWhiteSpace(value) && item.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)) value = Convert.ToBase64String(await _client.GetByteArrayAsync(uri, cancellationToken));
            if (!string.IsNullOrWhiteSpace(value)) images.Add(new NEARAIImage(value, item.TryGetProperty("revised_prompt", out var revised) ? revised.GetString() : null));
        }
        return images;
    }

    private sealed record NEARAIImage(string Base64, string? RevisedPrompt);
    private sealed record NEARAIImageResult(JsonElement Root, Dictionary<string, string> Headers, List<NEARAIImage> Images);


}
