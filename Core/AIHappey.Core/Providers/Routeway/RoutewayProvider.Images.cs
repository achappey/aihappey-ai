using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;

namespace AIHappey.Core.Providers.Routeway;

public partial class RoutewayProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";
    private const string ImageEditsEndpoint = "v1/images/edits";

    private static readonly JsonSerializerOptions RoutewayImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var providerOptions = GetRoutewayProviderOptions(request.ProviderOptions);
        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = request.Size,
            ["n"] = request.N,
            ["response_format"] = "b64_json"
        };

        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;
        MergeRoutewayOptions(payload, providerOptions);

        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("Routeway image edits require at least one input image.", nameof(request));

            payload["images"] = files.Select(GetImageBase64).ToArray();
            if (request.Mask is not null)
                payload["mask"] = GetImageBase64(request.Mask);
        }

        var result = await SendRoutewayImageRequestAsync(
            payload,
            isEdit ? ImageEditsEndpoint : ImageGenerationsEndpoint,
            isEdit ? "image edit" : "image generation",
            cancellationToken);
        var images = await ResolveRoutewayImagesAsDataUrlsAsync(result.Response, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Routeway image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Raw),
            Response = new HeaderResponseData
            {
                Timestamp = result.Response.Created > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(result.Response.Created).UtcDateTime
                    : DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = ToRoutewayPayload(options);
        payload["response_format"] = "b64_json";
        return (await SendRoutewayImageRequestAsync(payload, ImageGenerationsEndpoint, "image generation", cancellationToken)).Response;
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
                    Size = response.Size ?? options.Size,
                    Quality = response.Quality ?? options.Quality,
                    OutputFormat = response.OutputFormat ?? options.OutputFormat,
                    Background = response.Background ?? options.Background,
                    Usage = response.Usage
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var payload = ToRoutewayPayload(options);
        payload["response_format"] = "b64_json";
        payload["images"] = await GetEditImagesAsync(options, cancellationToken);

        var mask = await GetEditMaskAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(mask))
            payload["mask"] = mask;

        return (await SendRoutewayImageRequestAsync(payload, ImageEditsEndpoint, "image edit", cancellationToken)).Response;
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
                    Size = response.Size ?? options.Size,
                    Quality = response.Quality ?? options.Quality,
                    OutputFormat = response.OutputFormat ?? options.OutputFormat,
                    Background = response.Background ?? options.Background,
                    Usage = response.Usage
                };
        }
    }

    private static Dictionary<string, object?> ToRoutewayPayload(OpenAIImageGenerationRequest options)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(options, RoutewayImageJsonOptions), RoutewayImageJsonOptions) ?? [];
        payload.Remove("stream");
        return payload;
    }

    private static Dictionary<string, object?> ToRoutewayPayload(OpenAIImageEditRequest options)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(options, RoutewayImageJsonOptions), RoutewayImageJsonOptions) ?? [];
        payload.Remove("stream");
        payload.Remove("images");
        payload.Remove("mask");
        return payload;
    }

    private async Task<RoutewayImageResult> SendRoutewayImageRequestAsync(
        Dictionary<string, object?> payload,
        string endpoint,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RoutewayImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var httpResponse = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Routeway {operation} request failed ({(int)httpResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var response = JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, RoutewayImageJsonOptions)
            ?? throw new InvalidOperationException($"Routeway {operation} response was empty.");
        response.Data ??= [];
        await ResolveRoutewayImageBase64Async(response.Data, cancellationToken);
        return new RoutewayImageResult(response, root, httpResponse.GetHeaders());
    }

    private async Task ResolveRoutewayImageBase64Async(IEnumerable<OpenAIImageData> images, CancellationToken cancellationToken)
    {
        foreach (var image in images)
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json) || string.IsNullOrWhiteSpace(image.Url))
                continue;
            image.B64Json = Convert.ToBase64String(await _client.GetByteArrayAsync(image.Url, cancellationToken));
        }
    }

    private static Dictionary<string, JsonElement>? GetRoutewayProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions?.TryGetValue("routeway", out var options) == true && options.ValueKind == JsonValueKind.Object
            ? options.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
            : null;

    private static void MergeRoutewayOptions(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? options)
    {
        if (options is null)
            return;
        foreach (var option in options)
            payload[option.Key] = option.Value;
        payload["response_format"] = "b64_json";
    }

    private static string GetImageBase64(ImageFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Image data is required.", nameof(image));
        if (Uri.TryCreate(image.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException("Routeway image edits require base64-encoded input images.");
        return image.Data.RemoveDataUrlPrefix();
    }

    private static async Task<string[]> GetEditImagesAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        foreach (var file in options.ImageFiles ?? [])
            images.Add(Convert.ToBase64String(await ReadFormFileAsync(file, cancellationToken)));
        foreach (var reference in options.Images ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference.ImageUrl))
                continue;
            if (Uri.TryCreate(reference.ImageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                throw new NotSupportedException("Routeway image edits require base64-encoded input images, not remote URLs.");
            images.Add(reference.ImageUrl.RemoveDataUrlPrefix());
        }
        if (images.Count == 0)
            throw new ArgumentException("Routeway image edits require at least one input image.", nameof(options));
        return [.. images];
    }

    private static async Task<string?> GetEditMaskAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        if (options.MaskFile is not null)
            return Convert.ToBase64String(await ReadFormFileAsync(options.MaskFile, cancellationToken));
        return string.IsNullOrWhiteSpace(options.Mask?.ImageUrl) ? null : options.Mask.ImageUrl.RemoveDataUrlPrefix();
    }

    private static async Task<byte[]> ReadFormFileAsync(Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private async Task<List<string>> ResolveRoutewayImagesAsDataUrlsAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                images.Add($"data:{GetRoutewayImageMediaType(response.OutputFormat)};base64,{image.B64Json}");
            else if (!string.IsNullOrWhiteSpace(image.Url))
                images.Add($"data:image/png;base64,{Convert.ToBase64String(await _client.GetByteArrayAsync(image.Url, cancellationToken))}");
        }
        return images;
    }

    private static string GetRoutewayImageMediaType(string? outputFormat)
        => outputFormat?.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private sealed record RoutewayImageResult(OpenAIImagesResponse Response, JsonElement Raw, Dictionary<string, string> Headers);

}
