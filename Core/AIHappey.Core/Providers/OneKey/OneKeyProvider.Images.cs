using System.Globalization;
using System.Net.Http.Headers;
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

namespace AIHappey.Core.Providers.OneKey;

public partial class OneKeyProvider
{
    private const string OneKeyImageGenerationsEndpoint = "v1/images/generations";
    private const string OneKeyImageEditsEndpoint = "v1/images/edits";
    private static readonly JsonSerializerOptions OneKeyImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> OneKeyImageEditReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "prompt", "image", "image[]", "images", "images[]", "mask", "background",
        "input_fidelity", "moderation", "n", "output_compression", "output_format", "quality", "size", "user"
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        OneKeyImageResult result;
        if (files.Count > 0 || request.Mask is not null)
        {
            if (files.Count == 0) throw new ArgumentException("Image edits require at least one input image.", nameof(request));
            using var form = new MultipartFormDataContent();
            AddOneKeyFormValue(form, "model", request.Model);
            AddOneKeyFormValue(form, "prompt", request.Prompt);
            AddOneKeyFormValue(form, "n", request.N);
            AddOneKeyFormValue(form, "size", request.Size);
            AddOneKeyRawFormValues(form, request.GetProviderMetadata<JsonElement>(GetIdentifier()), OneKeyImageEditReserved);
            foreach (var image in files) AddOneKeyImagePart(form, "image", image);
            if (request.Mask is not null) AddOneKeyImagePart(form, "mask", request.Mask);
            result = await SendOneKeyImageAsync(OneKeyImageEditsEndpoint, form, "edit", cancellationToken);
        }
        else
        {
            var payload = CopyOneKeyObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
            SetOneKeyValue(payload, "model", request.Model);
            SetOneKeyValue(payload, "prompt", request.Prompt);
            SetOneKeyValue(payload, "n", request.N);
            SetOneKeyValue(payload, "size", request.Size);
            SetOneKeyValue(payload, "seed", request.Seed);
            SetOneKeyValue(payload, "aspect_ratio", request.AspectRatio);
            using var content = new StringContent(payload.ToJsonString(OneKeyImageJsonOptions), Encoding.UTF8, "application/json");
            result = await SendOneKeyImageAsync(OneKeyImageGenerationsEndpoint, content, "generation", cancellationToken);
        }

        var images = await ResolveOneKeyImagesAsync(result.Response.Data, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("OneKey returned no usable images.");
        return new ImageResponse
        {
            Images = images,
            Usage = result.Response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = result.Response.Usage.InputTokens,
                OutputTokens = result.Response.Usage.OutputTokens,
                TotalTokens = result.Response.Usage.TotalTokens
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = result.Response.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(result.Response.Created).UtcDateTime : DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CopyOneKeyObject(options.AdditionalProperties);
        SetOneKeyValue(payload, "model", options.Model);
        SetOneKeyValue(payload, "prompt", options.Prompt);
        SetOneKeyValue(payload, "background", options.Background);
        SetOneKeyValue(payload, "moderation", options.Moderation);
        SetOneKeyValue(payload, "n", options.N);
        SetOneKeyValue(payload, "output_compression", options.OutputCompression);
        SetOneKeyValue(payload, "output_format", options.OutputFormat);
        SetOneKeyValue(payload, "quality", options.Quality);
        SetOneKeyValue(payload, "response_format", options.ResponseFormat);
        SetOneKeyValue(payload, "size", options.Size);
        SetOneKeyValue(payload, "style", options.Style);
        SetOneKeyValue(payload, "user", options.User);
        using var content = new StringContent(payload.ToJsonString(OneKeyImageJsonOptions), Encoding.UTF8, "application/json");
        var result = await SendOneKeyImageAsync(OneKeyImageGenerationsEndpoint, content, "generation", cancellationToken);
        await NormalizeOneKeyImagesAsync(result.Response, cancellationToken);
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json)) continue;
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat, Quality = response.Quality ?? options.Quality,
                Size = response.Size ?? options.Size, Usage = index++ == 0 ? response.Usage : null
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        using var form = new MultipartFormDataContent();
        AddOneKeyFormValue(form, "model", options.Model);
        AddOneKeyFormValue(form, "prompt", options.Prompt);
        AddOneKeyFormValue(form, "background", options.Background);
        AddOneKeyFormValue(form, "input_fidelity", options.InputFidelity);
        AddOneKeyFormValue(form, "moderation", options.Moderation);
        AddOneKeyFormValue(form, "n", options.N);
        AddOneKeyFormValue(form, "output_compression", options.OutputCompression);
        AddOneKeyFormValue(form, "output_format", options.OutputFormat);
        AddOneKeyFormValue(form, "quality", options.Quality);
        AddOneKeyFormValue(form, "size", options.Size);
        AddOneKeyFormValue(form, "user", options.User);
        AddOneKeyRawFormValues(form, options.AdditionalProperties, OneKeyImageEditReserved);
        foreach (var file in options.ImageFiles ?? []) AddOneKeyFile(form, "image", file.OpenReadStream(), file.FileName, file.ContentType);
        foreach (var image in options.Images ?? []) AddOneKeyImageReference(form, "image", image);
        if (options.MaskFile is { } mask) AddOneKeyFile(form, "mask", mask.OpenReadStream(), mask.FileName, mask.ContentType);
        else if (options.Mask is not null) AddOneKeyImageReference(form, "mask", options.Mask);
        var result = await SendOneKeyImageAsync(OneKeyImageEditsEndpoint, form, "edit", cancellationToken);
        await NormalizeOneKeyImagesAsync(result.Response, cancellationToken);
        return result.Response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        var index = 0;
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json)) continue;
            yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat, Quality = response.Quality ?? options.Quality,
                Size = response.Size ?? options.Size, Usage = index++ == 0 ? response.Usage : null
            };
        }
    }

    private async Task<OneKeyImageResult> SendOneKeyImageAsync(string endpoint, HttpContent content, string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OneKey image {operation} failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var parsed = JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, OneKeyImageJsonOptions)
            ?? throw new InvalidOperationException($"OneKey image {operation} returned an invalid response.");
        if (parsed.Created <= 0) parsed.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new(document.RootElement.Clone(), parsed, response.GetHeaders());
    }

    private async Task NormalizeOneKeyImagesAsync(OpenAIImagesResponse response, CancellationToken cancellationToken)
    {
        foreach (var image in response.Data ?? [])
        {
#pragma warning disable CS0618
            if (!string.IsNullOrWhiteSpace(image.B64Json) || string.IsNullOrWhiteSpace(image.Url)) continue;
            image.B64Json = Convert.ToBase64String(await _client.GetByteArrayAsync(image.Url, cancellationToken));
            image.Url = null;
#pragma warning restore CS0618
        }
    }

    private async Task<List<string>> ResolveOneKeyImagesAsync(IEnumerable<OpenAIImageData>? data, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var image in data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json)) { result.Add($"data:image/png;base64,{image.B64Json}"); continue; }
#pragma warning disable CS0618
            if (string.IsNullOrWhiteSpace(image.Url)) continue;
            using var response = await _client.GetAsync(image.Url, cancellationToken);
            response.EnsureSuccessStatusCode();
            result.Add($"data:{response.Content.Headers.ContentType?.MediaType ?? "image/png"};base64,{Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(cancellationToken))}");
#pragma warning restore CS0618
        }
        return result;
    }

    private static JsonObject CopyOneKeyObject(JsonElement value) => value.ValueKind == JsonValueKind.Object ? JsonNode.Parse(value.GetRawText()) as JsonObject ?? [] : [];
    private static JsonObject CopyOneKeyObject(Dictionary<string, JsonElement>? values)
    {
        var result = new JsonObject();
        foreach (var value in values ?? []) result[value.Key] = JsonNode.Parse(value.Value.GetRawText());
        return result;
    }
    private static void SetOneKeyValue<T>(JsonObject payload, string name, T value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
        payload[name] = JsonSerializer.SerializeToNode(value, OneKeyImageJsonOptions);
    }
    private static void AddOneKeyRawFormValues(MultipartFormDataContent form, JsonElement values, IReadOnlySet<string> reserved)
    {
        if (values.ValueKind != JsonValueKind.Object) return;
        foreach (var value in values.EnumerateObject()) if (!reserved.Contains(value.Name)) AddOneKeyFormValue(form, value.Name, OneKeyFormText(value.Value));
    }
    private static void AddOneKeyRawFormValues(MultipartFormDataContent form, Dictionary<string, JsonElement>? values, IReadOnlySet<string> reserved)
    {
        foreach (var value in values ?? []) if (!reserved.Contains(value.Key)) AddOneKeyFormValue(form, value.Key, OneKeyFormText(value.Value));
    }
    private static string? OneKeyFormText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", _ => value.GetRawText()
    };
    private static void AddOneKeyFormValue(MultipartFormDataContent form, string name, object? value)
    {
        var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();
        if (!string.IsNullOrWhiteSpace(text)) form.Add(new StringContent(text, Encoding.UTF8), name);
    }
    private static void AddOneKeyImagePart(MultipartFormDataContent form, string name, ImageFile image)
    {
        if (image.Type is "url" or "file_id") { AddOneKeyFormValue(form, name, image.Data); return; }
        var (mediaType, bytes) = DecodeOneKeyImage(image.Data, image.MediaType);
        AddOneKeyFile(form, name, new MemoryStream(bytes), $"{name}.{OneKeyImageExtension(mediaType)}", mediaType);
    }
    private static void AddOneKeyImageReference(MultipartFormDataContent form, string name, OpenAIImageReference image)
    {
#pragma warning disable CS0618
        var value = image.ImageUrl ?? image.FileId;
#pragma warning restore CS0618
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { AddOneKeyFormValue(form, name, value); return; }
        var (mediaType, bytes) = DecodeOneKeyImage(value, "image/png");
        AddOneKeyFile(form, name, new MemoryStream(bytes), $"{name}.{OneKeyImageExtension(mediaType)}", mediaType);
    }
    private static void AddOneKeyFile(MultipartFormDataContent form, string name, Stream stream, string fileName, string? mediaType)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
        form.Add(content, name, fileName);
    }
    private static (string MediaType, byte[] Bytes) DecodeOneKeyImage(string value, string? fallback)
    {
        var mediaType = string.IsNullOrWhiteSpace(fallback) ? "image/png" : fallback;
        var base64 = value;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0) throw new FormatException("Invalid image data URL.");
            var header = value[5..comma];
            mediaType = header.Split(';')[0];
            base64 = value[(comma + 1)..];
        }
        return (mediaType, Convert.FromBase64String(base64));
    }
    private static string OneKeyImageExtension(string mediaType) => mediaType.EndsWith("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : mediaType.Split('/').LastOrDefault() ?? "png";
    private sealed record OneKeyImageResult(JsonElement Root, OpenAIImagesResponse Response, IDictionary<string, string> Headers);
}
