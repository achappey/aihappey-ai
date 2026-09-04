using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.PayPerQ;

public partial class PayPerQProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = CopyPayPerQOptions(request.ProviderOptions);
        payload["model"] = request.Model; payload["prompt"] = request.Prompt; payload["response_format"] = "b64_json";
        if (request.N is not null) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 0) payload["image_url"] = PayPerQImageValue(files[0]);
        var result = await SendPayPerQImageJsonAsync("v1/images/generations", payload, cancellationToken);
        var images = await ResolvePayPerQImagesAsync(result.Root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("PayPerQ image generation returned no images.");
        var warnings = new List<object>();
        if (files.Length > 1) warnings.Add(new { type = "unsupported", feature = "files", details = "Only the first source image was sent." });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask", details = "Use the OpenAI image edits endpoint for masks." });
        return new ImageResponse
        {
            Images = images.Select(x => $"data:{x.MediaType};base64,{x.Base64}"), Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(PayPerQCreated(result.Root)).UtcDateTime,
                Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CopyPayPerQOptions(options.AdditionalProperties);
        payload["model"] = options.Model; payload["prompt"] = options.Prompt;
        AddPayPerQGenerationFields(payload, options);
        var result = await SendPayPerQImageJsonAsync("v1/images/generations", payload, cancellationToken);
        return await ToPayPerQOpenAIImagesAsync(result.Root, options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, OutputFormat = response.OutputFormat,
                Quality = response.Quality, Size = response.Size
            };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var disposables = new List<IDisposable>();
        try
        {
            foreach (var file in options.ImageFiles ?? [])
            {
                var content = new StreamContent(file.OpenReadStream()); disposables.Add(content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                form.Add(content, "image[]", file.FileName);
            }
            foreach (var reference in options.Images ?? [])
            {
                if (string.IsNullOrWhiteSpace(reference.ImageUrl)) continue;
                var media = await DownloadPayPerQMediaAsync(reference.ImageUrl, false, cancellationToken);
                var content = new ByteArrayContent(Convert.FromBase64String(media.Base64)); disposables.Add(content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(media.MediaType);
                form.Add(content, "image[]", "image" + PayPerQImageExtension(media.MediaType));
            }
            if (options.MaskFile is not null)
            {
                var mask = new StreamContent(options.MaskFile.OpenReadStream()); disposables.Add(mask);
                mask.Headers.ContentType = MediaTypeHeaderValue.Parse(options.MaskFile.ContentType);
                form.Add(mask, "mask", options.MaskFile.FileName);
            }
            else if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl))
            {
                var media = await DownloadPayPerQMediaAsync(options.Mask.ImageUrl, false, cancellationToken);
                var mask = new ByteArrayContent(Convert.FromBase64String(media.Base64)); disposables.Add(mask);
                mask.Headers.ContentType = MediaTypeHeaderValue.Parse(media.MediaType);
                form.Add(mask, "mask", "mask" + PayPerQImageExtension(media.MediaType));
            }
            form.Add(new StringContent(options.Model), "model"); form.Add(new StringContent(options.Prompt), "prompt");
            var fields = CopyPayPerQOptions(options.AdditionalProperties);
            AddPayPerQEditFields(fields, options);
            fields.Remove("image"); fields.Remove("images"); fields.Remove("mask"); fields.Remove("model"); fields.Remove("prompt"); fields.Remove("stream");
            foreach (var (name, value) in fields)
                if (value is not null) form.Add(new StringContent(value is JsonElement json ? PayPerQJsonText(json)
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!), name);
            using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsurePayPerQSuccess(response, raw, "image edit");
            using var document = JsonDocument.Parse(raw);
            return await ToPayPerQOpenAIImagesAsync(document.RootElement.Clone(), options.OutputFormat, options.Quality, options.Size, cancellationToken);
        }
        finally { foreach (var disposable in disposables) disposable.Dispose(); }
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json, CreatedAt = response.Created, OutputFormat = response.OutputFormat,
                Quality = response.Quality, Size = response.Size
            };
    }

    private async Task<PayPerQImageResult> SendPayPerQImageJsonAsync(string path,
        Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        payload.Remove("stream");
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePayPerQSuccess(response, raw, "image generation");
        using var document = JsonDocument.Parse(raw);
        return new PayPerQImageResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<List<PayPerQMedia>> ResolvePayPerQImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var result = new List<PayPerQMedia>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var revised = PayPerQGetString(item, "revised_prompt");
            var base64 = PayPerQGetString(item, "b64_json", "base64");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                result.Add(new PayPerQMedia(Convert.ToBase64String(DecodePayPerQBase64(base64)),
                    PayPerQGetString(item, "content_type") ?? "image/png", revised));
                continue;
            }
            var url = PayPerQGetString(item, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var media = await DownloadPayPerQMediaAsync(url, false, cancellationToken);
                result.Add(media with { RevisedPrompt = revised });
            }
        }
        return result;
    }

    private async Task<OpenAIImagesResponse> ToPayPerQOpenAIImagesAsync(JsonElement root, string? outputFormat,
        string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ResolvePayPerQImagesAsync(root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("PayPerQ returned no images.");
        return new OpenAIImagesResponse
        {
            Created = PayPerQCreated(root), OutputFormat = outputFormat, Quality = quality, Size = size,
            Data = images.Select(x => new OpenAIImageData { B64Json = x.Base64, RevisedPrompt = x.RevisedPrompt }).ToList()
        };
    }

    private static void AddPayPerQGenerationFields(Dictionary<string, object?> payload, OpenAIImageGenerationRequest options)
    {
        if (options.N is not null) payload["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Quality)) payload["quality"] = options.Quality;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.OutputFormat)) payload["output_format"] = options.OutputFormat;
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat)) payload["response_format"] = options.ResponseFormat;
    }

    private static void AddPayPerQEditFields(Dictionary<string, object?> fields, OpenAIImageEditRequest options)
    {
        if (options.N is not null) fields["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Quality)) fields["quality"] = options.Quality;
        if (!string.IsNullOrWhiteSpace(options.Size)) fields["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.OutputFormat)) fields["output_format"] = options.OutputFormat;
        fields["response_format"] = "b64_json";
    }

    private static string PayPerQImageValue(ImageFile file)
        => file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data : $"data:{file.MediaType};base64,{file.Data}";
    private static string PayPerQImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };

    private sealed record PayPerQImageResult(JsonElement Root, Dictionary<string, string> Headers);
}
