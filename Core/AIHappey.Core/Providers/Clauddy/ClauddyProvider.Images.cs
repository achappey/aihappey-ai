using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Clauddy;

public partial class ClauddyProvider
{
    private const string ClauddyApiBase = "https://api.clauddy.com/v1/";
    private const string ClauddyGeminiImageModel = "gemini-3-pro-image-preview";
    private static readonly JsonSerializerOptions ClauddyImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly Regex ClauddyDataImageRegex = new(
        @"data:(?<mime>image/[a-zA-Z0-9.+-]+);base64,(?<data>[a-zA-Z0-9+/=\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var files = request.Files?.Where(static file => file is not null).ToArray() ?? [];
        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        ClauddyImageResult result;
        if (IsClauddyGeminiImageModel(request.Model))
        {
            if (request.Mask is not null)
                warnings.Add(new { type = "unsupported", feature = "mask", details = "Clauddy Gemini image editing does not expose a mask parameter." });
            result = await SendClauddyGeminiImageAsync(request.Model, request.Prompt, files.Select(ToClauddyDataUrl), cancellationToken);
        }
        else if (files.Length > 0 || request.Mask is not null)
        {
            if (files.Length == 0)
                throw new ArgumentException("Image edits require at least one input image.", nameof(request));
            result = await SendClauddyMultipartEditAsync(
                request.Model, request.Prompt, request.N, request.Size,
                ReadClauddyProviderString(request.ProviderOptions, "quality"),
                ReadClauddyProviderString(request.ProviderOptions, "background"),
                files.Select((file, index) => new ClauddyUpload(file.Data, file.MediaType, $"image-{index + 1}")).ToArray(),
                request.Mask is null ? null : new ClauddyUpload(request.Mask.Data, request.Mask.MediaType, "mask"),
                cancellationToken);
        }
        else
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["n"] = request.N,
                ["size"] = request.Size,
                ["quality"] = ReadClauddyProviderString(request.ProviderOptions, "quality"),
                ["background"] = ReadClauddyProviderString(request.ProviderOptions, "background"),
                ["output_format"] = ReadClauddyProviderString(request.ProviderOptions, "output_format", "outputFormat")
            };
            result = await SendClauddyJsonImageAsync("images/generations", payload, cancellationToken);
        }

        if (result.Images.Count == 0)
            throw new InvalidOperationException("Clauddy image response did not contain any images.");

        return new ImageResponse
        {
            Images = result.Images.Select(static image => $"data:{image.MediaType};base64,{image.Base64}"),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = ResolveClauddyTimestamp(result.Root),
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Usage = ReadClauddyUsage(result.Root)
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        ApplyAuthHeader();
        var result = IsClauddyGeminiImageModel(options.Model)
            ? await SendClauddyGeminiImageAsync(options.Model, options.Prompt, [], cancellationToken)
            : await SendClauddyJsonImageAsync("images/generations", options, cancellationToken);
        return ToClauddyOpenAIResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
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
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        ClauddyImageResult result;
        if (IsClauddyGeminiImageModel(options.Model))
        {
            var images = new List<string>();
            foreach (var file in options.ImageFiles ?? [])
                images.Add(await ToClauddyDataUrlAsync(file, cancellationToken));
            foreach (var image in options.Images ?? [])
                if (!string.IsNullOrWhiteSpace(image.ImageUrl)) images.Add(image.ImageUrl);
            if (images.Count == 0)
                throw new ArgumentException("Clauddy Gemini image edits require at least one input image.", nameof(options));
            result = await SendClauddyGeminiImageAsync(options.Model, options.Prompt, images, cancellationToken);
        }
        else
        {
            var uploads = new List<ClauddyUpload>();
            foreach (var file in options.ImageFiles ?? [])
                uploads.Add(new ClauddyUpload(await ReadFormFileBase64Async(file, cancellationToken), file.ContentType, Path.GetFileNameWithoutExtension(file.FileName)));
            if (uploads.Count == 0)
                throw new NotSupportedException("Clauddy gpt-image-2 edits require multipart image files.");
            ClauddyUpload? mask = options.MaskFile is null ? null : new ClauddyUpload(
                await ReadFormFileBase64Async(options.MaskFile, cancellationToken), options.MaskFile.ContentType, "mask");
            result = await SendClauddyMultipartEditAsync(options.Model, options.Prompt, options.N, options.Size,
                options.Quality, options.Background, uploads, mask, cancellationToken, options.InputFidelity);
        }
        return ToClauddyOpenAIResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
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
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size
                };
        }
    }

    private async Task<ClauddyImageResult> SendClauddyJsonImageAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ClauddyApiBase + endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ClauddyImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        return await SendAndReadClauddyImagesAsync(request, cancellationToken);
    }

    private async Task<ClauddyImageResult> SendClauddyMultipartEditAsync(
        string model, string prompt, int? n, string? size, string? quality, string? background,
        IReadOnlyCollection<ClauddyUpload> images, ClauddyUpload? mask, CancellationToken cancellationToken,
        string? inputFidelity = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(prompt), "prompt");
        if (n.HasValue) form.Add(new StringContent(n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "n");
        if (!string.IsNullOrWhiteSpace(size)) form.Add(new StringContent(size), "size");
        if (!string.IsNullOrWhiteSpace(quality)) form.Add(new StringContent(quality), "quality");
        if (!string.IsNullOrWhiteSpace(background)) form.Add(new StringContent(background), "background");
        if (!string.IsNullOrWhiteSpace(inputFidelity)) form.Add(new StringContent(inputFidelity), "input_fidelity");
        foreach (var image in images)
            AddClauddyUpload(form, image, "image[]");
        if (mask is not null) AddClauddyUpload(form, mask, "mask");
        using var request = new HttpRequestMessage(HttpMethod.Post, ClauddyApiBase + "images/edits") { Content = form };
        return await SendAndReadClauddyImagesAsync(request, cancellationToken);
    }

    private async Task<ClauddyImageResult> SendClauddyGeminiImageAsync(
        string model, string prompt, IEnumerable<string> imageUrls, CancellationToken cancellationToken)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        content.AddRange(imageUrls.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => (object)new { type = "image_url", image_url = new { url = value } }));
        var payload = new { model, messages = new[] { new { role = "user", content } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, ClauddyApiBase + "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ClauddyImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureClauddySuccess(response, raw, "Gemini image request");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var responseContent) && responseContent.ValueKind == JsonValueKind.String
                ? responseContent.GetString() : null;
        var images = ExtractClauddyDataImages(text);
        return new ClauddyImageResult(root, response.GetHeaders(), images);
    }

    private async Task<ClauddyImageResult> SendAndReadClauddyImagesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureClauddySuccess(response, raw, "image request");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = new List<ClauddyImage>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var base64 = item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String ? b64.GetString() : null;
                if (!string.IsNullOrWhiteSpace(base64))
                    images.Add(new ClauddyImage(base64, GuessClauddyImageType(root)));
            }
        }
        return new ClauddyImageResult(root, response.GetHeaders(), images);
    }

    private static void EnsureClauddySuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Clauddy {operation} failed ({(int)response.StatusCode})."
                : $"Clauddy {operation} failed ({(int)response.StatusCode}): {raw}");
    }

    private static void AddClauddyUpload(MultipartFormDataContent form, ClauddyUpload upload, string field)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(RemoveClauddyDataUrlPrefix(upload.Base64)); }
        catch (FormatException exception) { throw new ArgumentException("Clauddy image input must be base64 encoded.", nameof(upload), exception); }
        var content = new ByteArrayContent(bytes);
        var mediaType = string.IsNullOrWhiteSpace(upload.MediaType) ? MediaTypeNames.Image.Png : upload.MediaType;
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(content, field, upload.Name + ClauddyImageExtension(mediaType));
    }

    private static List<ClauddyImage> ExtractClauddyDataImages(string? content)
        => string.IsNullOrWhiteSpace(content) ? [] : ClauddyDataImageRegex.Matches(content)
            .Select(match => new ClauddyImage(match.Groups["data"].Value.Replace("\r", "").Replace("\n", ""), match.Groups["mime"].Value))
            .ToList();

    private static OpenAIImagesResponse ToClauddyOpenAIResponse(ClauddyImageResult result, string? background, string? outputFormat, string? quality, string? size)
        => new()
        {
            Created = new DateTimeOffset(ResolveClauddyTimestamp(result.Root)).ToUnixTimeSeconds(),
            Background = background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Data = result.Images.Select(static image => new OpenAIImageData { B64Json = image.Base64 }).ToList(),
            Usage = ReadClauddyOpenAIUsage(result.Root)
        };

    private static bool IsClauddyGeminiImageModel(string? model)
        => string.Equals(model?.Split('/').LastOrDefault(), ClauddyGeminiImageModel, StringComparison.OrdinalIgnoreCase);

    private static string ToClauddyDataUrl(ImageFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? file.Data : $"data:{file.MediaType};base64,{file.Data}";

    private static async Task<string> ToClauddyDataUrlAsync(Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken)
        => $"data:{(string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Image.Png : file.ContentType)};base64,{await ReadFormFileBase64Async(file, cancellationToken)}";

    private static async Task<string> ReadFormFileBase64Async(Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);
        return Convert.ToBase64String(memory.ToArray());
    }

    private static string RemoveClauddyDataUrlPrefix(string value)
    {
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? value : value[(marker + 8)..];
    }

    private static string ClauddyImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png"
    };

    private static string GuessClauddyImageType(JsonElement root)
        => root.TryGetProperty("output_format", out var format) && format.ValueKind == JsonValueKind.String
            ? format.GetString()?.ToLowerInvariant() switch { "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", _ => "image/png" }
            : "image/png";

    private static string? ReadClauddyProviderString(Dictionary<string, JsonElement>? options, params string[] names)
    {
        if (options is null || !options.TryGetValue("clauddy", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static DateTime ResolveClauddyTimestamp(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : DateTime.UtcNow;

    private static ImageUsageData? ReadClauddyUsage(JsonElement root)
    {
        var usage = ReadClauddyOpenAIUsage(root);
        return usage is null ? null : new ImageUsageData { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens };
    }

    private static OpenAIImageUsage? ReadClauddyOpenAIUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;
        return new OpenAIImageUsage
        {
            InputTokens = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens"),
            OutputTokens = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens"),
            TotalTokens = Number(usage, "total_tokens")
        };
    }

    private sealed record ClauddyUpload(string Base64, string? MediaType, string Name);
    private sealed record ClauddyImage(string Base64, string MediaType);
    private sealed record ClauddyImageResult(JsonElement Root, Dictionary<string, string> Headers, List<ClauddyImage> Images);
}
