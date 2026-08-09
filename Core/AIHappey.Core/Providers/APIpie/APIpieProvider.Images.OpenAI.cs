using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ValidateAPIpieImageCount(options.N);

        var payload = CreateAPIpieImagePayload(
            options.Model,
            options.Prompt,
            options.N,
            options.Size,
            options.Quality,
            options.Style,
            image: null,
            options.AdditionalProperties);

        return await SendAPIpieOpenAIImageRequestAsync(
            payload,
            options.Background,
            options.OutputFormat,
            options.Quality,
            options.Size,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // APIpie documents only a synchronous image response. Expose it as completed events.
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
                Background = response.Background,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size,
                Usage = response.Usage
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ValidateAPIpieImageCount(options.N);

        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("APIpie does not document mask-based image editing.");

        var referenceCount = options.Images?.Length ?? 0;
        var fileCount = options.ImageFiles?.Length ?? 0;
        if (referenceCount + fileCount != 1)
            throw new ArgumentException("APIpie image edits require exactly one input image.", nameof(options));

        string imageUrl;
        if (referenceCount == 1)
        {
            var reference = options.Images![0];
            if (!string.IsNullOrWhiteSpace(reference.FileId))
                throw new NotSupportedException("APIpie does not support OpenAI file IDs for image edits.");
            if (string.IsNullOrWhiteSpace(reference.ImageUrl))
                throw new ArgumentException("The APIpie edit image must contain an image_url.", nameof(options));

            imageUrl = IsHttpUrl(reference.ImageUrl)
                ? reference.ImageUrl
                : await UploadAPIpieImageAsync(reference.ImageUrl, cancellationToken);
        }
        else
        {
            imageUrl = await UploadAPIpieImageAsync(options.ImageFiles![0], cancellationToken);
        }

        var payload = CreateAPIpieImagePayload(
            options.Model,
            options.Prompt,
            options.N,
            options.Size,
            options.Quality,
            style: null,
            imageUrl,
            options.AdditionalProperties);

        return await SendAPIpieOpenAIImageRequestAsync(
            payload,
            options.Background,
            options.OutputFormat,
            options.Quality,
            options.Size,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // APIpie documents only a synchronous image response. Expose it as completed events.
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
                Background = response.Background,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size,
                Usage = response.Usage
            };
        }
    }

    private static Dictionary<string, object?> CreateAPIpieImagePayload(
        string model,
        string prompt,
        int? n,
        string? size,
        string? quality,
        string? style,
        string? image,
        Dictionary<string, JsonElement>? additionalProperties)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["n"] = n ?? 1,
            ["size"] = size,
            ["quality"] = quality,
            ["response_format"] = "b64_json",
            ["style"] = style,
            ["image"] = image
        };

        var reserved = new HashSet<string>(payload.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in additionalProperties ?? [])
        {
            if (!reserved.Contains(name))
                payload[name] = JsonSerializer.Deserialize<object?>(value.GetRawText(), imageSettings);
        }

        return payload;
    }

    private async Task<OpenAIImagesResponse> SendAPIpieOpenAIImageRequestAsync(
        Dictionary<string, object?> payload,
        string? background,
        string? outputFormat,
        string? quality,
        string? size,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, imageSettings),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"APIpie image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("APIpie image generation response did not include a data array.");

        var images = new List<OpenAIImageData>();
        foreach (var item in data.EnumerateArray())
        {
            var base64 = GetOptionalString(item, "b64_json");
            if (string.IsNullOrWhiteSpace(base64) && GetOptionalString(item, "url") is { } url)
            {
                using var imageResponse = await _client.GetAsync(url, cancellationToken);
                if (!imageResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException($"APIpie image download failed ({(int)imageResponse.StatusCode}): {url}");
                base64 = Convert.ToBase64String(await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken));
            }

            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("APIpie returned an image without base64 data or a downloadable URL.");

            images.Add(new OpenAIImageData
            {
                B64Json = base64,
                RevisedPrompt = GetOptionalString(item, "revised_prompt")
            });
        }

        if (images.Count == 0)
            throw new InvalidOperationException("APIpie image generation returned no images.");

        return new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp)
                ? timestamp
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images,
            Background = string.Equals(background, "auto", StringComparison.OrdinalIgnoreCase) ? null : background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size
        };
    }

    private async Task<string> UploadAPIpieImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        return await UploadAPIpieImageAsync(stream, file.FileName, file.ContentType, cancellationToken);
    }

    private async Task<string> UploadAPIpieImageAsync(string image, CancellationToken cancellationToken)
    {
        var contentType = "image/png";
        var base64 = image;
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = image.IndexOf(',');
            if (comma < 0 || !image[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("APIpie image data URLs must contain base64 image data.", nameof(image));
            contentType = image[5..image.IndexOf(';')];
            base64 = image[(comma + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("APIpie image input must be an HTTP(S) URL, base64 string, or base64 data URL.", nameof(image), exception);
        }

        using var stream = new MemoryStream(bytes);
        return await UploadAPIpieImageAsync(stream, GetUploadFileName(contentType), contentType, cancellationToken);
    }

    private async Task<string> UploadAPIpieImageAsync(
        Stream stream,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        if (!string.IsNullOrWhiteSpace(contentType))
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "image.png" : fileName);

        using var response = await _client.PostAsync("urlshare", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"APIpie image upload failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var url = GetOptionalString(document.RootElement, "url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("APIpie image upload response did not include a URL.");
        return url;
    }

    private static void ValidateAPIpieImageCount(int? n)
    {
        if (n is > 1)
            throw new NotSupportedException("APIpie currently supports only one generated image per request (n=1).");
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? GetOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetUploadFileName(string contentType)
        => contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => "image.jpg",
            "image/gif" => "image.gif",
            "image/bmp" => "image.bmp",
            "image/tiff" => "image.tiff",
            "image/webp" => "image.webp",
            "image/svg+xml" => "image.svg",
            _ => "image.png"
        };
}
