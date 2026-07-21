using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using Microsoft.AspNetCore.Http;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IONOS;

public partial class IONOSProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";
    private const string ImageEditsEndpoint = "v1/images/edits";
    private const string DefaultSize = "1024*1024";

    private static readonly JsonSerializerOptions IonosImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.ResolveOpenAIImageGenerationModel(),
            ["prompt"] = options.Prompt,
            ["n"] = options.N,
            ["size"] = NormalizeSize(options.Size),
            ["output_format"] = options.OutputFormat,
            ["user"] = options.User
        };

        return await SendImageGenerationAsync(payload, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IONOS documents a synchronous image response, so expose a compatible completed event.
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();

        var sourceCount = (options.Images?.Length ?? 0) + (options.ImageFiles?.Length ?? 0);
        if (sourceCount != 1)
            throw new ArgumentException("IONOS image edits require exactly one input image.", nameof(options));

        var url = options.Images?.SingleOrDefault() is { } imageReference
            ? GetImageUrl(imageReference)
            : await GetImageDataUrlAsync(options.ImageFiles!.Single(), cancellationToken);
        var maskImage = options.Mask is not null
            ? GetMaskDataUrl(options.Mask)
            : options.MaskFile is null
                ? null
                : await GetImageDataUrlAsync(options.MaskFile, cancellationToken);

        return await SendImageEditAsync(
            options.ResolveOpenAIImageEditModel(),
            options.Prompt,
            url,
            maskImage,
            options.N,
            options.Size,
            options.OutputFormat,
            options.User,
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IONOS documents a synchronous image response, so expose a compatible completed event.
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

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken = default)
        => this.FromImageRequest(options, cancellationToken);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var outputFormat = GetString(metadata, "output_format", "outputFormat") ?? "png";
        var user = GetString(metadata, "user");
        var size = NormalizeSize(request.Size);
        var sourceImages = request.Files?.Where(file => file is not null).ToList() ?? [];
        var isEdit = sourceImages.Count > 0 || request.Mask is not null;
        OpenAIImagesResponse result;

        if (isEdit)
        {
            if (sourceImages.Count == 0)
                throw new ArgumentException("IONOS image edits require an input image.", nameof(request));
            if (sourceImages.Count > 1)
                warnings.Add(new { type = "unsupported", feature = "files", details = "IONOS image edits use only the first input image." });

            result = await SendImageEditAsync(
                request.Model,
                request.Prompt,
                ToImageUrl(sourceImages[0]),
                request.Mask is null ? null : ToDataUrl(request.Mask),
                request.N,
                size,
                outputFormat,
                user,
                cancellationToken);
        }
        else
        {
            result = await SendImageGenerationAsync(new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["n"] = request.N,
                ["size"] = size,
                ["output_format"] = outputFormat,
                ["user"] = user
            }, cancellationToken);
        }

        var images = ExtractImages(result, outputFormat);
        if (images.Count == 0)
            throw new InvalidOperationException($"IONOS {(isEdit ? "image edit" : "image generation")} returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ToImageUsage(result.Usage),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = new()
            {
                Timestamp = result.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(result.Created).UtcDateTime : DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<OpenAIImagesResponse> SendImageGenerationAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ImageGenerationsEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, IonosImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await SendAndReadImagesAsync(request, "image generation", cancellationToken);
    }

    private async Task<OpenAIImagesResponse> SendImageEditAsync(
        string model,
        string prompt,
        string url,
        string? maskImage,
        int? n,
        string? size,
        string? outputFormat,
        string? user,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        AddFormField(content, "model", model);
        AddFormField(content, "prompt", prompt);
        AddFormField(content, "url", url);
        AddFormField(content, "mask_image", maskImage);
        AddFormField(content, "n", n?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddFormField(content, "size", NormalizeSize(size));
        AddFormField(content, "output_format", outputFormat);
        AddFormField(content, "user", user);

        using var request = new HttpRequestMessage(HttpMethod.Post, ImageEditsEndpoint) { Content = content };
        return await SendAndReadImagesAsync(request, "image edit", cancellationToken);
    }

    private async Task<OpenAIImagesResponse> SendAndReadImagesAsync(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"IONOS {operation} failed ({(int)response.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, IonosImageJson)
            ?? throw new InvalidOperationException($"IONOS {operation} returned an invalid response.");
    }

    private static void AddFormField(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            content.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static string NormalizeSize(string? size)
        => string.IsNullOrWhiteSpace(size) ? DefaultSize : size.Trim().Replace("x", "*", StringComparison.OrdinalIgnoreCase);

    private static string GetImageUrl(OpenAIImageReference image)
    {
        if (!string.IsNullOrWhiteSpace(image.FileId))
            throw new NotSupportedException("IONOS image edits do not support file_id image references.");
        if (string.IsNullOrWhiteSpace(image.ImageUrl))
            throw new ArgumentException("Image references require image_url.", nameof(image));
        return image.ImageUrl;
    }

    private static string GetMaskDataUrl(OpenAIImageReference mask)
    {
        var value = GetImageUrl(mask);
        if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("IONOS image edit masks must be base64 data URLs.", nameof(mask));
        return value;
    }

    private static async Task<string> GetImageDataUrlAsync(IFormFile image, CancellationToken cancellationToken)
    {
        await using var stream = image.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var mediaType = string.IsNullOrWhiteSpace(image.ContentType) ? MediaTypeNames.Image.Png : image.ContentType;
        return Convert.ToBase64String(memory.ToArray()).ToDataUrl(mediaType);
    }

    private static string ToImageUrl(ImageFile image)
        => image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? image.Data
            : ToDataUrl(image);

    private static string ToDataUrl(ImageFile image)
        => image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? image.Data
            : image.Data.ToDataUrl(string.IsNullOrWhiteSpace(image.MediaType) ? MediaTypeNames.Image.Png : image.MediaType);

    private static List<string> ExtractImages(OpenAIImagesResponse response, string outputFormat)
    {
        var mediaType = outputFormat.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

        return response.Data?.Select(image => !string.IsNullOrWhiteSpace(image.B64Json)
                ? image.B64Json.ToDataUrl(mediaType)
                : image.Url)
            .Where(image => !string.IsNullOrWhiteSpace(image))
            .Cast<string>()
            .ToList() ?? [];
    }

    private static ImageUsageData? ToImageUsage(OpenAIImageUsage? usage)
        => usage is null ? null : new ImageUsageData
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private static string? GetString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }
}
