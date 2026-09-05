using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.AIsa;

public partial class AIsaProvider
{
    private const string AIsaImageGenerationsEndpoint = "v1/images/generations";
    private const string AIsaImageEditsEndpoint = "v1/images/edits";

    private static readonly JsonSerializerOptions AIsaImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        List<object> warnings = [];
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.GetProviderMetadata<JsonElement>(GetIdentifier()).ValueKind == JsonValueKind.Object)
            warnings.Add(new { type = "unsupported", feature = "providerOptions" });

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        AIsaImageResult result;

        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("AIsa image edits require at least one input image.", nameof(request));

            List<MemoryStream> streams = [];
            try
            {
                result = await SendAIsaImageEditAsync(CreateAIsaImageEditRequest(request, files, streams), cancellationToken);
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }
        }
        else
        {
            result = await SendAIsaImageGenerationAsync(CreateAIsaImageGenerationRequest(request), cancellationToken);
        }

        var response = result.Response;
        var images = await PersistAIsaImagesAsync(response, isEdit ? "image edit" : "image generation", cancellationToken);
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new()
            {
                Timestamp = response.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime : DateTime.UtcNow,
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
        return (await SendAIsaImageGenerationAsync(options, cancellationToken)).Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = (await SendAIsaImageGenerationAsync(options, cancellationToken)).Response;

        foreach (var image in await ToAIsaBase64ImagesAsync(response, cancellationToken))
        {
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Usage = response.Usage
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return (await SendAIsaImageEditAsync(options, cancellationToken)).Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var response = (await SendAIsaImageEditAsync(options, cancellationToken)).Response;

        foreach (var image in await ToAIsaBase64ImagesAsync(response, cancellationToken))
        {
            yield return new OpenAIImageEditCompleted
            {
                B64Json = image,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Usage = response.Usage
            };
        }
    }

    private async Task<AIsaImageResult> SendAIsaImageGenerationAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["prompt"] = options.Prompt,
            ["n"] = options.N,
            ["size"] = options.Size
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, AIsaImageGenerationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AIsaImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        return await SendAIsaImageRequestAsync(request, "Image generation", cancellationToken);
    }

    private async Task<AIsaImageResult> SendAIsaImageEditAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var content = await CreateAIsaImageEditContentAsync(options, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, AIsaImageEditsEndpoint) { Content = content };
        return await SendAIsaImageRequestAsync(request, "Image edit", cancellationToken);
    }

    private async Task<AIsaImageResult> SendAIsaImageRequestAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIsa {operation.ToLowerInvariant()} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var parsed = root.Deserialize<OpenAIImagesResponse>(AIsaImageJsonOptions)
            ?? throw new InvalidOperationException($"AIsa {operation.ToLowerInvariant()} returned an invalid response.");
        if (parsed.Data is null)
            throw new InvalidOperationException($"AIsa {operation.ToLowerInvariant()} response did not contain a data array.");

        return new AIsaImageResult(root, response.GetHeaders(), parsed);
    }

    private async Task<MultipartFormDataContent> CreateAIsaImageEditContentAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken)
    {
        var form = new MultipartFormDataContent();
        try
        {
            AddAIsaFormValue(form, "model", options.Model);
            AddAIsaFormValue(form, "prompt", options.Prompt);
            AddAIsaFormValue(form, "n", options.N?.ToString(CultureInfo.InvariantCulture));
            AddAIsaFormValue(form, "size", options.Size);

            var imageCount = 0;
            foreach (var file in options.ImageFiles ?? [])
            {
                form.Add(await CreateAIsaFileContentAsync(file, cancellationToken), "image", AIsaFileName(file, $"image-{++imageCount}"));
            }
            foreach (var reference in options.Images ?? [])
            {
                form.Add(await CreateAIsaReferenceContentAsync(reference, cancellationToken), "image", $"image-{++imageCount}.png");
            }

            if (options.MaskFile is not null)
                form.Add(await CreateAIsaFileContentAsync(options.MaskFile, cancellationToken), "mask", AIsaFileName(options.MaskFile, "mask"));
            else if (options.Mask is not null)
                form.Add(await CreateAIsaReferenceContentAsync(options.Mask, cancellationToken), "mask", "mask.png");

            return form;
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }

    private async Task<ByteArrayContent> CreateAIsaReferenceContentAsync(
        OpenAIImageReference reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.ImageUrl))
            throw new ArgumentException("AIsa image references require image_url.", nameof(reference));

        if (reference.ImageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = reference.ImageUrl.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("AIsa image data URL is invalid.", nameof(reference));
            var mediaType = reference.ImageUrl[5..reference.ImageUrl.IndexOf(';')];
            return CreateAIsaByteContent(Convert.FromBase64String(reference.ImageUrl[(comma + 1)..]), mediaType);
        }

        using var download = await _client.GetAsync(reference.ImageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!download.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Failed to download AIsa input image ({(int)download.StatusCode}).");
        return CreateAIsaByteContent(bytes, download.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<ByteArrayContent> CreateAIsaFileContentAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return CreateAIsaByteContent(buffer.ToArray(), file.ContentType);
    }

    private static ByteArrayContent CreateAIsaByteContent(byte[] bytes, string? mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Png : mediaType);
        return content;
    }

    private static void AddAIsaFormValue(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), name);
    }

    private static string AIsaFileName(IFormFile file, string fallbackName)
        => string.IsNullOrWhiteSpace(file.FileName)
            ? fallbackName + AIsaImageExtension(file.ContentType)
            : file.FileName;

    private static OpenAIImageGenerationRequest CreateAIsaImageGenerationRequest(ImageRequest request)
        => new() { Model = request.Model, Prompt = request.Prompt, N = request.N, Size = request.Size };

    private static OpenAIImageEditRequest CreateAIsaImageEditRequest(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        List<MemoryStream> streams)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            ImageFiles = files.Select((file, index) => CreateAIsaFormFile(file, "image", $"image-{index + 1}", streams)).ToArray(),
            MaskFile = request.Mask is null ? null : CreateAIsaFormFile(request.Mask, "mask", "mask", streams)
        };

    private static IFormFile CreateAIsaFormFile(ImageFile file, string fieldName, string fallbackName, List<MemoryStream> streams)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException("AIsa generic image edits require base64 image data.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix()); }
        catch (FormatException exception) { throw new ArgumentException("Image data must be base64 encoded.", nameof(file), exception); }

        var stream = new MemoryStream(bytes, writable: false);
        streams.Add(stream);
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
        return new FormFile(stream, 0, stream.Length, fieldName, fallbackName + AIsaImageExtension(mediaType))
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private async Task<List<string>> PersistAIsaImagesAsync(OpenAIImagesResponse response, string operation, CancellationToken cancellationToken)
    {
        var images = await ToAIsaDataUrlsAsync(response, cancellationToken);
        return images.Count > 0 ? images : throw new InvalidOperationException($"AIsa {operation} response did not contain generated images.");
    }

    private async Task<List<string>> ToAIsaDataUrlsAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
    {
        List<string> images = [];
        var fallbackMediaType = AIsaImageMediaType(response.OutputFormat);
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add(image.B64Json.ToDataUrl(fallbackMediaType));
                continue;
            }
#pragma warning disable CS0618 // AIsa documents URL responses.
            if (string.IsNullOrWhiteSpace(image.Url))
                continue;
            if (image.Url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(image.Url);
                continue;
            }

            using var download = await _client.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
#pragma warning restore CS0618
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to persist the short-lived AIsa image URL ({(int)download.StatusCode}).");
            var mediaType = download.Content.Headers.ContentType?.MediaType;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(string.IsNullOrWhiteSpace(mediaType) ? fallbackMediaType : mediaType));
        }
        return images;
    }

    private async Task<List<string>> ToAIsaBase64ImagesAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
        => (await ToAIsaDataUrlsAsync(response, cancellationToken))
            .Select(image => image.RemoveDataUrlPrefix())
            .ToList();

    private static string AIsaImageMediaType(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
        "webp" => "image/webp",
        _ => MediaTypeNames.Image.Png
    };

    private static string AIsaImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        MediaTypeNames.Image.Jpeg or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png"
    };

    private sealed record AIsaImageResult(
        JsonElement Root,
        Dictionary<string, string> Headers,
        OpenAIImagesResponse Response);
}
