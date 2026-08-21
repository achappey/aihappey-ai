using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LaoZhang;

public partial class LaoZhangProvider
{
    private const string ImageGenerationsEndpoint = "v1/images/generations";
    private const string ImageEditsEndpoint = "v1/images/edits";

    private static readonly JsonSerializerOptions LaoZhangImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0 || request.Mask is not null;
        LaoZhangImageResult result;

        if (isEdit)
        {
            if (files.Count == 0)
                throw new ArgumentException("LaoZhang image edits require at least one input image.", nameof(request));

            using var form = new MultipartFormDataContent();
            AddFormValue(form, "model", request.Model);
            AddFormValue(form, "prompt", request.Prompt);
            AddFormValue(form, "n", request.N);
            AddFormValue(form, "size", request.Size);
            AddRawFormProperties(form, request.GetProviderMetadata<JsonElement>(GetIdentifier()), ImageEditReservedFields);
            foreach (var file in files)
                AddImagePart(form, "image", file);
            if (request.Mask is not null)
                AddImagePart(form, "mask", request.Mask);

            result = await SendImageRequestAsync(ImageEditsEndpoint, form, "edit", cancellationToken);
        }
        else
        {
            var payload = CopyRawObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
            Set(payload, "model", request.Model);
            Set(payload, "prompt", request.Prompt);
            Set(payload, "n", request.N);
            Set(payload, "size", request.Size);
            Set(payload, "seed", request.Seed);
            Set(payload, "aspect_ratio", request.AspectRatio);

            using var content = new StringContent(payload.ToJsonString(LaoZhangImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);
            result = await SendImageRequestAsync(ImageGenerationsEndpoint, content, "generation", cancellationToken);
        }

        var images = await ResolveImageDataUrlsAsync(result.Response.Data, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException($"LaoZhang image {(isEdit ? "edit" : "generation")} returned no usable images.");

        return new ImageResponse
        {
            Images = images,
            Usage = ToVercelUsage(result.Response.Usage),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = ToTimestamp(result.Response.Created),
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
        var payload = CopyRawObject(options.AdditionalProperties);
        Set(payload, "model", options.Model);
        Set(payload, "prompt", options.Prompt);
        Set(payload, "background", options.Background);
        Set(payload, "moderation", options.Moderation);
        Set(payload, "n", options.N);
        Set(payload, "output_compression", options.OutputCompression);
        Set(payload, "output_format", options.OutputFormat);
        Set(payload, "quality", options.Quality);
        Set(payload, "response_format", options.ResponseFormat);
        Set(payload, "size", options.Size);
        Set(payload, "style", options.Style);
        Set(payload, "user", options.User);

        using var content = new StringContent(payload.ToJsonString(LaoZhangImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);
        var result = await SendImageRequestAsync(ImageGenerationsEndpoint, content, "generation", cancellationToken);
        await NormalizeOpenAIImagesAsync(result.Response, cancellationToken);
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Quality = response.Quality ?? options.Quality,
                Size = response.Size ?? options.Size,
                Usage = index++ == 0 ? response.Usage : null
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        using var form = new MultipartFormDataContent();
        AddFormValue(form, "model", options.Model);
        AddFormValue(form, "prompt", options.Prompt);
        AddFormValue(form, "background", options.Background);
        AddFormValue(form, "input_fidelity", options.InputFidelity);
        AddFormValue(form, "moderation", options.Moderation);
        AddFormValue(form, "n", options.N);
        AddFormValue(form, "output_compression", options.OutputCompression);
        AddFormValue(form, "output_format", options.OutputFormat);
        AddFormValue(form, "quality", options.Quality);
        AddFormValue(form, "size", options.Size);
        AddFormValue(form, "user", options.User);
        AddRawFormProperties(form, options.AdditionalProperties, ImageEditReservedFields);

        foreach (var file in options.ImageFiles ?? [])
            AddFormFile(form, "image", file.OpenReadStream(), file.FileName, file.ContentType);
        foreach (var image in options.Images ?? [])
            AddImageReference(form, "image", image);
        if (options.MaskFile is { } maskFile)
            AddFormFile(form, "mask", maskFile.OpenReadStream(), maskFile.FileName, maskFile.ContentType);
        else if (options.Mask is not null)
            AddImageReference(form, "mask", options.Mask);

        var result = await SendImageRequestAsync(ImageEditsEndpoint, form, "edit", cancellationToken);
        await NormalizeOpenAIImagesAsync(result.Response, cancellationToken);
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Quality = response.Quality ?? options.Quality,
                Size = response.Size ?? options.Size,
                Usage = index++ == 0 ? response.Usage : null
            };
        }
    }

    private static readonly HashSet<string> ImageEditReservedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "prompt", "image", "image[]", "images", "images[]", "mask", "background",
        "input_fidelity", "moderation", "n", "output_compression", "output_format", "quality", "size", "user"
    };

    private async Task<LaoZhangImageResult> SendImageRequestAsync(
        string endpoint,
        HttpContent content,
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LaoZhang image {operation} failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var parsed = JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, LaoZhangImageJsonOptions)
            ?? throw new InvalidOperationException($"LaoZhang image {operation} returned an invalid response.");
        if (parsed.Created <= 0)
            parsed.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new LaoZhangImageResult(root, parsed, GetResponseHeaders(response));
    }

    private async Task NormalizeOpenAIImagesAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
    {
        foreach (var image in response.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json) || string.IsNullOrWhiteSpace(image.Url))
                continue;

            image.B64Json = (await DownloadImageAsync(image.Url, cancellationToken)).Base64;
#pragma warning disable CS0618
            image.Url = null;
#pragma warning restore CS0618
        }
    }

    private async Task<List<string>> ResolveImageDataUrlsAsync(IEnumerable<OpenAIImageData>? data, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        foreach (var image in data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
            {
                images.Add(ToDataUrl(image.B64Json, "image/png"));
                continue;
            }

#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(image.Url))
            {
                var downloaded = await DownloadImageAsync(image.Url, cancellationToken);
                images.Add(ToDataUrl(downloaded.Base64, downloaded.MediaType));
            }
#pragma warning restore CS0618
        }
        return images;
    }

    private async Task<LaoZhangDownloadedImage> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new LaoZhangDownloadedImage(
            Convert.ToBase64String(bytes),
            response.Content.Headers.ContentType?.MediaType ?? "image/png");
    }

    private static JsonObject CopyRawObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(element.GetRawText()) as JsonObject ?? []
            : [];

    private static JsonObject CopyRawObject(Dictionary<string, JsonElement>? properties)
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties ?? [])
            result[name] = JsonNode.Parse(value.GetRawText());
        return result;
    }

    private static void Set<T>(JsonObject payload, string name, T value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            return;
        payload[name] = JsonSerializer.SerializeToNode(value, LaoZhangImageJsonOptions);
    }

    private static void AddRawFormProperties(MultipartFormDataContent form, JsonElement properties, HashSet<string> reserved)
    {
        if (properties.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in properties.EnumerateObject())
            if (!reserved.Contains(property.Name))
                AddFormValue(form, property.Name, ToFormValue(property.Value));
    }

    private static void AddRawFormProperties(MultipartFormDataContent form, Dictionary<string, JsonElement>? properties, HashSet<string> reserved)
    {
        foreach (var (name, value) in properties ?? [])
            if (!reserved.Contains(name))
                AddFormValue(form, name, ToFormValue(value));
    }

    private static string? ToFormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => value.GetRawText()
        };

    private static void AddFormValue(MultipartFormDataContent form, string name, object? value)
    {
        var text = value switch
        {
            null => null,
            string stringValue => stringValue,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        if (!string.IsNullOrWhiteSpace(text))
            form.Add(new StringContent(text, Encoding.UTF8), name);
    }

    private static void AddImagePart(MultipartFormDataContent form, string name, ImageFile image)
    {
        if (image.Type is "url" or "file_id")
        {
            AddFormValue(form, name, image.Data);
            return;
        }

        var (mediaType, bytes) = DecodeImage(image.Data, image.MediaType);
        AddFormFile(form, name, new MemoryStream(bytes), $"{name}.{GetExtension(mediaType)}", mediaType);
    }

    private static void AddImageReference(MultipartFormDataContent form, string name, OpenAIImageReference image)
    {
        if (string.IsNullOrWhiteSpace(image.ImageUrl))
        {
#pragma warning disable CS0618
            AddFormValue(form, name, image.FileId);
#pragma warning restore CS0618
            return;
        }

        if (!image.ImageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            AddFormValue(form, name, image.ImageUrl);
            return;
        }

        var (mediaType, bytes) = DecodeImage(image.ImageUrl, "image/png");
        AddFormFile(form, name, new MemoryStream(bytes), $"{name}.{GetExtension(mediaType)}", mediaType);
    }

    private static void AddFormFile(MultipartFormDataContent form, string name, Stream stream, string fileName, string? mediaType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType);
        form.Add(content, name, fileName);
    }

    private static (string MediaType, byte[] Bytes) DecodeImage(string value, string? fallbackMediaType)
    {
        var mediaType = string.IsNullOrWhiteSpace(fallbackMediaType) ? "image/png" : fallbackMediaType;
        var base64 = value;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                throw new FormatException("Invalid image data URL.");
            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            mediaType = semicolon < 0 ? header : header[..semicolon];
            base64 = value[(comma + 1)..];
        }
        return (mediaType, Convert.FromBase64String(base64));
    }

    private static string ToDataUrl(string value, string mediaType)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? value : $"data:{mediaType};base64,{value}";

    private static string GetExtension(string mediaType)
        => mediaType.Split('/').LastOrDefault()?.Split('+').FirstOrDefault() switch
        {
            "jpeg" => "jpg",
            "svg" => "svg",
            "webp" => "webp",
            _ => "png"
        };

    private static IDictionary<string, string> GetResponseHeaders(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);

    private static DateTime ToTimestamp(long created)
        => created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime : DateTime.UtcNow;

    private static ImageUsageData? ToVercelUsage(OpenAIImageUsage? usage)
        => usage is null ? null : new ImageUsageData
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private sealed record LaoZhangImageResult(
        JsonElement Root,
        OpenAIImagesResponse Response,
        IDictionary<string, string> Headers);

    private sealed record LaoZhangDownloadedImage(string Base64, string MediaType);
}
