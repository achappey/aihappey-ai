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
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.CheaperInference;

public partial class CheaperInferenceProvider
{
    private static readonly JsonSerializerOptions CheaperInferenceMediaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.Where(static file => file is not null).ToArray() ?? [];
        CheaperInferenceJsonResult result;
        if (files.Length > 0 || request.Mask is not null)
            result = await SendCheaperInferenceVercelImageEditAsync(request, files, cancellationToken);
        else
        {
            var payload = ReadCheaperInferenceOptions(request.ProviderOptions);
            payload["model"] = request.Model;
            payload["prompt"] = request.Prompt;
            SetCheaperInferenceValue(payload, "n", request.N);
            SetCheaperInferenceValue(payload, "size", request.Size);
            SetCheaperInferenceValue(payload, "aspect_ratio", request.AspectRatio);
            SetCheaperInferenceValue(payload, "seed", request.Seed);
            result = await SendCheaperInferenceJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        }

        var images = await ResolveCheaperInferenceImagesAsync(result.Root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("Cheaper Inference image response did not contain any usable images.");
        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            Usage = ReadCheaperInferenceImageUsage(result.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadCheaperInferenceTimestamp(result.Root),
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(options, CheaperInferenceMediaJson), CheaperInferenceMediaJson)!;
        payload["response_format"] = "b64_json";
        payload.Remove("stream");
        payload.Remove("partial_images");
        var result = await SendCheaperInferenceJsonAsync(HttpMethod.Post, "v1/images/generations", payload, "image generation", cancellationToken);
        return await ToCheaperInferenceOpenAIImagesAsync(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background,
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size, Usage = response.Usage
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        CheaperInferenceJsonResult result;
        if (options.ImageFiles?.Length > 0 || options.MaskFile is not null)
            result = await SendCheaperInferenceMultipartImageEditAsync(options, cancellationToken);
        else
        {
            var payload = BuildCheaperInferenceJsonEditPayload(options);
            result = await SendCheaperInferenceJsonAsync(HttpMethod.Post, "v1/images/edits", payload, "image edit", cancellationToken);
        }
        return await ToCheaperInferenceOpenAIImagesAsync(result.Root, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background,
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size, Usage = response.Usage
            };
        }
    }

    private async Task<CheaperInferenceJsonResult> SendCheaperInferenceVercelImageEditAsync(
        ImageRequest request, ImageFile[] files, CancellationToken cancellationToken)
    {
        var allReferencesAreHttp = files.Length > 0 && files.All(static file => IsCheaperInferenceHttpUrl(file.Data));
        if (allReferencesAreHttp && request.Mask is null)
        {
            var payload = ReadCheaperInferenceOptions(request.ProviderOptions);
            payload["model"] = request.Model;
            payload["prompt"] = request.Prompt;
            payload["input_references"] = files.Select(static file => file.Data).ToArray();
            SetCheaperInferenceValue(payload, "n", request.N);
            SetCheaperInferenceValue(payload, "size", request.Size);
            SetCheaperInferenceValue(payload, "aspect_ratio", request.AspectRatio);
            SetCheaperInferenceValue(payload, "seed", request.Seed);
            return await SendCheaperInferenceJsonAsync(HttpMethod.Post, "v1/images/edits", payload, "image edit", cancellationToken);
        }

        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        foreach (var pair in ReadCheaperInferenceOptions(request.ProviderOptions)) AddCheaperInferenceFormValue(form, pair.Key, pair.Value);
        AddCheaperInferenceFormValue(form, "model", request.Model);
        AddCheaperInferenceFormValue(form, "prompt", request.Prompt);
        AddCheaperInferenceFormValue(form, "n", request.N);
        AddCheaperInferenceFormValue(form, "size", request.Size);
        AddCheaperInferenceFormValue(form, "aspect_ratio", request.AspectRatio);
        AddCheaperInferenceFormValue(form, "seed", request.Seed);
        for (var index = 0; index < files.Length; index++)
            form.Add(ToCheaperInferenceImageContent(files[index]), "image", $"image-{index}.{ImageExtension(files[index].MediaType)}");
        if (request.Mask is not null)
            form.Add(ToCheaperInferenceImageContent(request.Mask), "mask", "mask.png");
        return await SendCheaperInferenceMultipartAsync(form, "image edit", cancellationToken);
    }

    private async Task<CheaperInferenceJsonResult> SendCheaperInferenceMultipartImageEditAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        AddCheaperInferenceFormValue(form, "model", options.Model);
        AddCheaperInferenceFormValue(form, "prompt", options.Prompt);
        AddCheaperInferenceFormValue(form, "background", options.Background);
        AddCheaperInferenceFormValue(form, "input_fidelity", options.InputFidelity);
        AddCheaperInferenceFormValue(form, "moderation", options.Moderation);
        AddCheaperInferenceFormValue(form, "n", options.N);
        AddCheaperInferenceFormValue(form, "output_compression", options.OutputCompression);
        AddCheaperInferenceFormValue(form, "output_format", options.OutputFormat);
        AddCheaperInferenceFormValue(form, "quality", options.Quality);
        AddCheaperInferenceFormValue(form, "size", options.Size);
        AddCheaperInferenceFormValue(form, "user", options.User);
        foreach (var pair in options.AdditionalProperties ?? []) AddCheaperInferenceFormValue(form, pair.Key, pair.Value);
        for (var index = 0; index < (options.ImageFiles?.Length ?? 0); index++)
            form.Add(await ToCheaperInferenceFileContentAsync(options.ImageFiles![index], cancellationToken), "image", options.ImageFiles[index].FileName);
        if (options.MaskFile is not null)
            form.Add(await ToCheaperInferenceFileContentAsync(options.MaskFile, cancellationToken), "mask", options.MaskFile.FileName);
        return await SendCheaperInferenceMultipartAsync(form, "image edit", cancellationToken);
    }

    private async Task<CheaperInferenceJsonResult> SendCheaperInferenceMultipartAsync(MultipartFormDataContent form, string operation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/edits") { Content = form };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadCheaperInferenceJsonAsync(response, operation, cancellationToken);
    }

    private static Dictionary<string, object?> BuildCheaperInferenceJsonEditPayload(OpenAIImageEditRequest options)
    {
        var payload = new Dictionary<string, object?>();
        foreach (var pair in options.AdditionalProperties ?? []) payload[pair.Key] = pair.Value.Clone();
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["input_references"] = (options.Images ?? []).Select(static image => image.ImageUrl)
            .Where(static url => !string.IsNullOrWhiteSpace(url)).ToArray();
        SetCheaperInferenceValue(payload, "mask", options.Mask?.ImageUrl);
        SetCheaperInferenceValue(payload, "background", options.Background);
        SetCheaperInferenceValue(payload, "input_fidelity", options.InputFidelity);
        SetCheaperInferenceValue(payload, "moderation", options.Moderation);
        SetCheaperInferenceValue(payload, "n", options.N);
        SetCheaperInferenceValue(payload, "output_compression", options.OutputCompression);
        SetCheaperInferenceValue(payload, "output_format", options.OutputFormat);
        SetCheaperInferenceValue(payload, "quality", options.Quality);
        SetCheaperInferenceValue(payload, "size", options.Size);
        SetCheaperInferenceValue(payload, "user", options.User);
        return payload;
    }

    private async Task<OpenAIImagesResponse> ToCheaperInferenceOpenAIImagesAsync(
        JsonElement root, string? background, string? outputFormat, string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ResolveCheaperInferenceImageDataAsync(root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("Cheaper Inference image response did not contain any usable images.");
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(ReadCheaperInferenceTimestamp(root)).ToUnixTimeSeconds(),
            Background = background, OutputFormat = outputFormat, Quality = quality, Size = size,
            Data = images, Usage = ReadCheaperInferenceOpenAIImageUsage(root)
        };
    }

    private async Task<List<string>> ResolveCheaperInferenceImagesAsync(JsonElement root, CancellationToken cancellationToken)
        => (await ResolveCheaperInferenceImageDataAsync(root, cancellationToken))
            .Where(static image => !string.IsNullOrWhiteSpace(image.B64Json))
            .Select(static image => $"data:image/png;base64,{image.B64Json}").ToList();

    private async Task<List<OpenAIImageData>> ResolveCheaperInferenceImageDataAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var result = new List<OpenAIImageData>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var revisedPrompt = ReadCheaperInferenceString(item, "revised_prompt");
            var base64 = ReadCheaperInferenceString(item, "b64_json", "base64", "data");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                result.Add(new OpenAIImageData { B64Json = RemoveCheaperInferenceDataUrlPrefix(base64), RevisedPrompt = revisedPrompt });
                continue;
            }
            var url = ReadCheaperInferenceString(item, "url", "image_url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            using var response = await _downloadClient.GetAsync(url, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Cheaper Inference image download failed ({(int)response.StatusCode}).");
            result.Add(new OpenAIImageData { B64Json = Convert.ToBase64String(bytes), RevisedPrompt = revisedPrompt });
        }
        return result;
    }

    private static ImageUsageData? ReadCheaperInferenceImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new ImageUsageData
        {
            InputTokens = ReadCheaperInferenceInt(usage, "input_tokens", "prompt_tokens"),
            OutputTokens = ReadCheaperInferenceInt(usage, "output_tokens", "completion_tokens"),
            TotalTokens = ReadCheaperInferenceInt(usage, "total_tokens")
        };
    }

    private static OpenAIImageUsage? ReadCheaperInferenceOpenAIImageUsage(JsonElement root)
    {
        var usage = ReadCheaperInferenceImageUsage(root);
        return usage is null ? null : new OpenAIImageUsage
        {
            InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens
        };
    }

    private static ByteArrayContent ToCheaperInferenceImageContent(ImageFile file)
    {
        if (IsCheaperInferenceHttpUrl(file.Data)) throw new NotSupportedException("HTTP image references must use the JSON image-edit form.");
        var bytes = Convert.FromBase64String(RemoveCheaperInferenceDataUrlPrefix(file.Data));
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
        return content;
    }

    private static async Task<ByteArrayContent> ToCheaperInferenceFileContentAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var content = new ByteArrayContent(memory.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Application.Octet : file.ContentType);
        return content;
    }

    private static void AddCheaperInferenceFormValue(MultipartFormDataContent form, string name, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
        var serialized = value switch
        {
            string textValue => textValue,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
            JsonElement element => element.GetRawText(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value, CheaperInferenceMediaJson)
        };
        form.Add(new StringContent(serialized, Encoding.UTF8), name);
    }

    private static string ImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png"
    };
}
