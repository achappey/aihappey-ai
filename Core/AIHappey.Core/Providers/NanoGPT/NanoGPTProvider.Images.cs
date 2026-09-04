using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = CopyNanoGPTOptions(request.ProviderOptions);
        payload["model"] = request.Model; payload["prompt"] = request.Prompt; payload["response_format"] = "b64_json";
        if (request.N is not null) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var files = request.Files?.ToArray() ?? [];
        if (files.Length == 1) payload["imageDataUrl"] = NanoGPTImageValue(files[0]);
        else if (files.Length > 1) payload["imageDataUrls"] = files.Select(NanoGPTImageValue).ToArray();
        if (request.Mask is not null) payload["maskDataUrl"] = NanoGPTImageValue(request.Mask);
        var result = await SendNanoGPTImageJsonAsync("v1/images/generations", payload, cancellationToken);
        var images = await ResolveNanoGPTImagesAsync(result.Root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("NanoGPT image generation returned no images.");
        return new ImageResponse
        {
            Images = images.Select(x => $"data:{x.MediaType};base64,{x.Base64}"), Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(NanoGPTCreated(result.Root)).UtcDateTime,
                Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CopyNanoGPTOptions(options.AdditionalProperties);
        payload["model"] = options.Model; payload["prompt"] = options.Prompt;
        AddNanoGPTGenerationFields(payload, options);
        var result = await SendNanoGPTImageJsonAsync("v1/images/generations", payload, cancellationToken);
        return await ToNanoGPTOpenAIImagesAsync(result.Root, options.OutputFormat, options.Quality, options.Size, cancellationToken);
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

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
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
                var media = await DownloadNanoGPTMediaAsync(reference.ImageUrl, false, cancellationToken);
                var content = new ByteArrayContent(Convert.FromBase64String(media.Base64)); disposables.Add(content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(media.MediaType);
                form.Add(content, "image[]", "image" + NanoGPTImageExtension(media.MediaType));
            }
            if (options.MaskFile is not null)
            {
                var mask = new StreamContent(options.MaskFile.OpenReadStream()); disposables.Add(mask);
                mask.Headers.ContentType = MediaTypeHeaderValue.Parse(options.MaskFile.ContentType);
                form.Add(mask, "mask", options.MaskFile.FileName);
            }
            else if (!string.IsNullOrWhiteSpace(options.Mask?.ImageUrl))
            {
                var media = await DownloadNanoGPTMediaAsync(options.Mask.ImageUrl, false, cancellationToken);
                var mask = new ByteArrayContent(Convert.FromBase64String(media.Base64)); disposables.Add(mask);
                mask.Headers.ContentType = MediaTypeHeaderValue.Parse(media.MediaType);
                form.Add(mask, "mask", "mask" + NanoGPTImageExtension(media.MediaType));
            }
            form.Add(new StringContent(options.Model), "model"); form.Add(new StringContent(options.Prompt), "prompt");
            var fields = CopyNanoGPTOptions(options.AdditionalProperties);
            AddNanoGPTEditFields(fields, options);
            fields.Remove("image"); fields.Remove("images"); fields.Remove("mask"); fields.Remove("model"); fields.Remove("prompt"); fields.Remove("stream");
            foreach (var (name, value) in fields)
                if (value is not null) form.Add(new StringContent(value is JsonElement json ? NanoGPTJsonText(json)
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!), name);
            using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureNanoGPTSuccess(response, raw, "image edit");
            using var document = JsonDocument.Parse(raw);
            return await ToNanoGPTOpenAIImagesAsync(document.RootElement.Clone(), options.OutputFormat, options.Quality, options.Size, cancellationToken);
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

    private async Task<NanoGPTImageResult> SendNanoGPTImageJsonAsync(string path, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        payload.Remove("stream"); ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureNanoGPTSuccess(response, raw, "image generation");
        using var document = JsonDocument.Parse(raw);
        return new NanoGPTImageResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<List<NanoGPTMedia>> ResolveNanoGPTImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var result = new List<NanoGPTMedia>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var revised = NanoGPTGetString(item, "revised_prompt");
            var base64 = NanoGPTGetString(item, "b64_json", "base64");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                result.Add(new NanoGPTMedia(Convert.ToBase64String(DecodeNanoGPTBase64(base64)),
                    NanoGPTGetString(item, "content_type") ?? "image/png", revised)); continue;
            }
            var url = NanoGPTGetString(item, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var media = await DownloadNanoGPTMediaAsync(url, false, cancellationToken);
                result.Add(media with { RevisedPrompt = revised });
            }
        }
        return result;
    }

    private async Task<OpenAIImagesResponse> ToNanoGPTOpenAIImagesAsync(JsonElement root, string? outputFormat,
        string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ResolveNanoGPTImagesAsync(root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("NanoGPT returned no images.");
        return new OpenAIImagesResponse
        {
            Created = NanoGPTCreated(root), OutputFormat = outputFormat, Quality = quality, Size = size,
            Data = images.Select(x => new OpenAIImageData { B64Json = x.Base64, RevisedPrompt = x.RevisedPrompt }).ToList()
        };
    }

    private static void AddNanoGPTGenerationFields(Dictionary<string, object?> payload, OpenAIImageGenerationRequest options)
    {
        if (options.N is not null) payload["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Quality)) payload["quality"] = options.Quality;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.OutputFormat)) payload["output_format"] = options.OutputFormat;
        payload["response_format"] = "b64_json";
    }

    private static void AddNanoGPTEditFields(Dictionary<string, object?> fields, OpenAIImageEditRequest options)
    {
        if (options.N is not null) fields["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Quality)) fields["quality"] = options.Quality;
        if (!string.IsNullOrWhiteSpace(options.Size)) fields["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.OutputFormat)) fields["output_format"] = options.OutputFormat;
        fields["response_format"] = "b64_json";
    }

    private static string NanoGPTImageValue(ImageFile file)
        => file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data : $"data:{file.MediaType};base64,{file.Data}";
    private static string NanoGPTImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
    private sealed record NanoGPTImageResult(JsonElement Root, Dictionary<string, string> Headers);
}
