using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OnlyPixAI;

public partial class OnlyPixAIProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";
    private const string ImageEditsEndpoint = "v1/images/edits";

    private static readonly JsonSerializerOptions PixCodeImageJsonOptions = new(JsonSerializerDefaults.Web)
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
        if (request.N is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(request), "n must be between 1 and 10.");

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        if (isEdit && files.Count != 1)
            throw new ArgumentException("PixCode image edits require exactly one input image.", nameof(request));

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = CreateImagePayload(
            request.Model,
            request.Prompt,
            request.N,
            request.Size,
            null,
            null,
            null,
            null,
            null,
            isEdit ? ToPixCodeImageInput(files[0]) : null,
            request.Mask is null ? null : ToPixCodeImageInput(request.Mask),
            GetPixCodeProviderOptions(request));
        var result = await SendImageRequestAsync(isEdit, payload, cancellationToken);

        return new ImageResponse
        {
            Images = result.Images,
            Warnings = warnings,
            Usage = result.Usage,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Raw, result.Cost),
            Response = new()
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
        var result = await SendImageRequestAsync(false, CreateImagePayload(
            options.Model,
            options.Prompt,
            options.N,
            options.Size,
            options.Quality,
            options.Background,
            options.Moderation,
            options.OutputFormat,
            options.OutputCompression,
            null,
            null,
            options.AdditionalProperties), cancellationToken);

        return ToOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
        var files = await ResolveEditFilesAsync(options, cancellationToken);
        if (files.Count != 1)
            throw new ArgumentException("PixCode image edits require exactly one input image.", nameof(options));

        var mask = await ResolveEditMaskAsync(options, cancellationToken);
        var result = await SendImageRequestAsync(true, CreateImagePayload(
            options.Model,
            options.Prompt,
            options.N,
            options.Size,
            options.Quality,
            options.Background,
            options.Moderation,
            options.OutputFormat,
            options.OutputCompression,
            ToPixCodeImageInput(files[0]),
            mask,
            options.AdditionalProperties), cancellationToken);

        return ToOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
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
                Background = response.Background,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size,
                Usage = response.Usage
            };
        }
    }

    private async Task<PixCodeImageResult> SendImageRequestAsync(
        bool isEdit,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, isEdit ? ImageEditsEndpoint : ImageGenerationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, PixCodeImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PixCode image {(isEdit ? "edit" : "generation")} failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await DownloadImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException($"PixCode image {(isEdit ? "edit" : "generation")} returned no usable image URLs.");

        return new PixCodeImageResult(
            root,
            images,
            response.GetHeaders(),
            TryGetDecimal(root, "cost"),
            ExtractUsage(root));
    }

    private static Dictionary<string, object?> CreateImagePayload(
        string model,
        string prompt,
        int? n,
        string? size,
        string? quality,
        string? background,
        string? moderation,
        string? outputFormat,
        int? outputCompression,
        string? image,
        string? mask,
        Dictionary<string, JsonElement>? additionalProperties)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["n"] = n,
            ["size"] = size,
            ["quality"] = quality,
            ["background"] = background,
            ["moderation"] = moderation,
            ["output_format"] = outputFormat,
            ["output_compression"] = outputCompression,
            ["image"] = image,
            ["mask"] = mask
        };

        var reserved = new HashSet<string>(payload.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in additionalProperties ?? [])
        {
            if (!reserved.Contains(name))
                payload[name] = value.Clone();
        }

        return payload;
    }

    private static string ToPixCodeImageInput(ImageFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Input image data cannot be empty.", nameof(image));
        if (image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return image.Data;

        var mediaType = string.IsNullOrWhiteSpace(image.MediaType) ? MediaTypeNames.Image.Png : image.MediaType;
        return $"data:{mediaType};base64,{image.Data}";
    }

    private async Task<List<string>> DownloadImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("images", out var imageArray) || imageArray.ValueKind != JsonValueKind.Array)
            return [];

        var images = new List<string>();
        foreach (var image in imageArray.EnumerateArray())
        {
            if (!image.TryGetProperty("url", out var urlProperty)
                || urlProperty.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var url))
            {
                throw new InvalidOperationException("PixCode image response contained an image without a valid URL.");
            }

            using var response = await _client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"PixCode generated image download failed ({(int)response.StatusCode}): {url}");

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType
                ?? GetImageMediaType(url, root)
                ?? MediaTypeNames.Image.Png;
            images.Add($"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        }

        return images;
    }

    private static OpenAIImagesResponse ToOpenAIImagesResponse(
        PixCodeImageResult result,
        string? background,
        string? outputFormat,
        string? quality,
        string? size)
        => new()
        {
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Usage = result.Usage is null ? null : new()
            {
                InputTokens = result.Usage.InputTokens,
                OutputTokens = result.Usage.OutputTokens,
                TotalTokens = result.Usage.TotalTokens
            },
            Data = result.Images.Select(image => new OpenAIImageData { B64Json = ExtractBase64(image) }).ToList()
        };

    private static async Task<List<ImageFile>> ResolveEditFilesAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        var files = new List<ImageFile>();
        foreach (var file in options.ImageFiles ?? [])
            files.Add(await ToImageFileAsync(file, cancellationToken));
        foreach (var reference in options.Images ?? [])
            files.Add(ToImageFile(reference));
        return files;
    }

    private static async Task<string?> ResolveEditMaskAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
        => options.MaskFile is not null
            ? ToPixCodeImageInput(await ToImageFileAsync(options.MaskFile, cancellationToken))
            : options.Mask is null ? null : ToPixCodeImageInput(ToImageFile(options.Mask));

    private static async Task<ImageFile> ToImageFileAsync(global::Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return new ImageFile
        {
            MediaType = string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Image.Png : file.ContentType,
            Data = Convert.ToBase64String(memory.ToArray())
        };
    }

    private static ImageFile ToImageFile(OpenAIImageReference reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.FileId))
            throw new NotSupportedException("PixCode image edits do not support file_id image references.");
        if (string.IsNullOrWhiteSpace(reference.ImageUrl))
            throw new ArgumentException("Image references require image_url.", nameof(reference));
        return new ImageFile { Data = reference.ImageUrl, MediaType = MediaTypeNames.Image.Png };
    }

    private static string ExtractBase64(string dataUrl)
    {
        var separator = dataUrl.IndexOf(',');
        return separator < 0 ? dataUrl : dataUrl[(separator + 1)..];
    }

    private static string? GetImageMediaType(Uri url, JsonElement root)
    {
        if (TryGetImageString(root, "output_format") is { } format)
            return format.ToLowerInvariant() switch
            {
                "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
                "webp" => "image/webp",
                "png" => MediaTypeNames.Image.Png,
                _ => null
            };

        return Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpeg" or ".jpg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".png" => MediaTypeNames.Image.Png,
            _ => null
        };
    }

    private static ImageUsageData? ExtractUsage(JsonElement root)
    {
        var input = TryGetInt32(root, "input_tokens", "inputTokens");
        var output = TryGetInt32(root, "output_tokens", "outputTokens");
        var total = TryGetInt32(root, "total_tokens", "totalTokens") ?? (input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null);
        return input.HasValue || output.HasValue || total.HasValue
            ? new ImageUsageData { InputTokens = input, OutputTokens = output, TotalTokens = total }
            : null;
    }

    private static int? TryGetInt32(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }

    private static decimal? TryGetDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var cost))
            return cost;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out cost) ? cost : null;
    }

    private static string? TryGetImageString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, JsonElement>? GetPixCodeProviderOptions(ImageRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(nameof(OnlyPixAI).ToLowerInvariant(), out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return options.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private sealed record PixCodeImageResult(
        JsonElement Raw,
        List<string> Images,
        Dictionary<string, string> Headers,
        decimal? Cost,
        ImageUsageData? Usage);
}
