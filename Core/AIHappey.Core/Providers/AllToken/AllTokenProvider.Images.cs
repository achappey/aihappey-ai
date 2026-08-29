using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.AllToken;

public partial class AllTokenProvider
{
    private static readonly JsonSerializerOptions AllTokenImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        var files = request.Files?.Where(static file => file is not null).ToArray() ?? [];
        var metadata = ReadAllTokenMetadata(request.ProviderOptions);
        var warnings = new List<object>();
        if (request.Seed.HasValue) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        AllTokenImageTask task;
        if (files.Length > 0 || request.Mask is not null)
        {
            if (files.Length == 0) throw new ArgumentException("AllToken image edits require a source image.", nameof(request));
            if (files.Length > 1) warnings.Add(new { type = "unsupported", feature = "files", details = "AllToken accepts one edit source; only the first image was used." });
            var source = await UploadAllTokenImageAsync(files[0].Data, files[0].MediaType, "image_edit_source", cancellationToken);
            AllTokenUpload? mask = request.Mask is null ? null : await UploadAllTokenImageAsync(request.Mask.Data, request.Mask.MediaType, "image_edit_mask", cancellationToken);
            var payload = BuildAllTokenImagePayload(request.Model, request.Prompt, request.N, request.Size, metadata);
            payload["image_upload_id"] = source.UploadId;
            if (mask is not null) payload["mask_upload_id"] = mask.UploadId;
            task = await CreateAndPollAllTokenImageAsync("v1/images/edits", payload, cancellationToken);
        }
        else
        {
            task = await CreateAndPollAllTokenImageAsync("v1/images/generations/async",
                BuildAllTokenImagePayload(request.Model, request.Prompt, request.N, request.Size, metadata), cancellationToken);
        }

        var images = await ResolveAllTokenImagesAsync(task.Root, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("AllToken image task completed without images.");
        return new ImageResponse
        {
            Images = images.Select(static image => $"data:{image.MediaType};base64,{image.Base64}"),
            Warnings = warnings,
            Usage = ToAllTokenImageUsage(task.Root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(task.Root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadAllTokenImageTimestamp(task.Root),
                Headers = task.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(options, AllTokenImageJson), AllTokenImageJson)!;
        payload.Remove("response_format");
        payload.Remove("stream");
        payload.Remove("partial_images");
        var task = await CreateAndPollAllTokenImageAsync("v1/images/generations/async", payload, cancellationToken);
        return await ToAllTokenOpenAIImagesAsync(task, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
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
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size
            };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var sourceFile = options.ImageFiles?.FirstOrDefault()
            ?? throw new NotSupportedException("AllToken edits require a local multipart source image.");
        var source = await UploadAllTokenFormFileAsync(sourceFile, "image_edit_source", cancellationToken);
        AllTokenUpload? mask = options.MaskFile is null ? null : await UploadAllTokenFormFileAsync(options.MaskFile, "image_edit_mask", cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model, ["prompt"] = options.Prompt, ["image_upload_id"] = source.UploadId,
            ["mask_upload_id"] = mask?.UploadId, ["n"] = options.N, ["size"] = options.Size,
            ["quality"] = options.Quality, ["output_format"] = options.OutputFormat,
            ["output_compression"] = options.OutputCompression, ["background"] = options.Background,
            ["moderation"] = options.Moderation
        };
        AddAllTokenAdditionalProperties(payload, options.AdditionalProperties);
        var task = await CreateAndPollAllTokenImageAsync("v1/images/edits", payload, cancellationToken);
        return await ToAllTokenOpenAIImagesAsync(task, options.Background, options.OutputFormat, options.Quality, options.Size, cancellationToken);
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
                OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size
            };
        }
    }

    private async Task<AllTokenImageTask> CreateAndPollAllTokenImageAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AllTokenImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureAllTokenImageSuccess(createResponse, raw, "create");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = ReadAllTokenString(root, "id") ?? throw new InvalidOperationException("AllToken image response did not contain an id.");
        var headers = createResponse.GetHeaders();

        return await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async ct =>
            {
                using var pollResponse = await _client.GetAsync($"v1/images/generations/{Uri.EscapeDataString(taskId)}", ct);
                var pollRaw = await pollResponse.Content.ReadAsStringAsync(ct);
                EnsureAllTokenImageSuccess(pollResponse, pollRaw, "poll");
                using var pollDocument = JsonDocument.Parse(pollRaw);
                return new AllTokenImageTask(pollDocument.RootElement.Clone(), pollResponse.GetHeaders());
            },
            task => IsAllTokenImageTerminal(ReadAllTokenString(task.Root, "status")),
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5), null, cancellationToken);
    }

    private async Task<AllTokenUpload> UploadAllTokenImageAsync(string value, string? mediaType, string purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Image data is required.", nameof(value));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(RemoveAllTokenDataUrlPrefix(value)); }
        catch (FormatException exception) { throw new ArgumentException("AllToken image inputs must be base64 encoded.", nameof(value), exception); }
        return await UploadAllTokenBytesAsync(bytes, string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Png : mediaType, purpose, cancellationToken);
    }

    private async Task<AllTokenUpload> UploadAllTokenFormFileAsync(IFormFile file, string purpose, CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);
        return await UploadAllTokenBytesAsync(memory.ToArray(), string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Image.Png : file.ContentType, purpose, cancellationToken);
    }

    private async Task<AllTokenUpload> UploadAllTokenBytesAsync(byte[] bytes, string mediaType, string purpose, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var md5 = Convert.ToBase64String(MD5.HashData(bytes));
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var payload = new { purpose, content_type = mediaType, content_length = bytes.LongLength, content_md5 = md5, checksum_sha256 = sha256 };
        using var presignRequest = new HttpRequestMessage(HttpMethod.Post, "v1/uploads/presign")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AllTokenImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var presignResponse = await _client.SendAsync(presignRequest, cancellationToken);
        var raw = await presignResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureAllTokenImageSuccess(presignResponse, raw, "upload presign");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var uploadUrl = ReadAllTokenString(root, "upload_url") ?? throw new InvalidOperationException("AllToken presign response missing upload_url.");
        var uploadId = ReadAllTokenString(root, "upload_id") ?? throw new InvalidOperationException("AllToken presign response missing upload_id.");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(bytes) };
        if (root.TryGetProperty("required_headers", out var requiredHeaders) && requiredHeaders.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in requiredHeaders.EnumerateObject())
            {
                var value = header.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!uploadRequest.Content.Headers.TryAddWithoutValidation(header.Name, value))
                    uploadRequest.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }
        using var uploadResponse = await _uploadClient.SendAsync(uploadRequest, cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"AllToken R2 upload failed ({(int)uploadResponse.StatusCode}): {await uploadResponse.Content.ReadAsStringAsync(cancellationToken)}");
        return new AllTokenUpload(uploadId);
    }

    private async Task<OpenAIImagesResponse> ToAllTokenOpenAIImagesAsync(AllTokenImageTask task, string? background, string? outputFormat, string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ResolveAllTokenImagesAsync(task.Root, cancellationToken);
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(ReadAllTokenImageTimestamp(task.Root)).ToUnixTimeSeconds(), Background = background,
            OutputFormat = outputFormat ?? ReadAllTokenString(task.Root, "output_format"), Quality = quality ?? ReadAllTokenString(task.Root, "quality"),
            Size = size ?? ReadAllTokenString(task.Root, "size"),
            Data = images.Select(static image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList(),
            Usage = ToAllTokenOpenAIUsage(task.Root)
        };
    }

    private async Task<List<AllTokenImage>> ResolveAllTokenImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var status = ReadAllTokenString(root, "status");
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AllToken image generation ended with status '{status}': {root.GetRawText()}");
        var result = new List<AllTokenImage>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var mediaType = ReadAllTokenString(item, "mime_type") ?? AllTokenImageMediaType(ReadAllTokenString(root, "output_format"));
            var base64 = ReadAllTokenString(item, "b64_json");
            if (string.IsNullOrWhiteSpace(base64) && ReadAllTokenString(item, "r2_url") is { } url)
            {
                using var response = await _uploadClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                mediaType = response.Content.Headers.ContentType?.MediaType ?? mediaType;
                base64 = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            if (!string.IsNullOrWhiteSpace(base64)) result.Add(new AllTokenImage(base64, mediaType, ReadAllTokenString(item, "revised_prompt")));
        }
        return result;
    }

    private static Dictionary<string, object?> BuildAllTokenImagePayload(string model, string prompt, int? n, string? size, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?> { ["model"] = model, ["prompt"] = prompt, ["n"] = n, ["size"] = size };
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                if (!payload.ContainsKey(property.Name)) payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static void AddAllTokenAdditionalProperties(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? additional)
    {
        if (additional is null) return;
        foreach (var property in additional) if (!payload.ContainsKey(property.Key)) payload[property.Key] = property.Value.Clone();
    }

    private static JsonElement ReadAllTokenMetadata(Dictionary<string, JsonElement>? options)
        => options is not null && options.TryGetValue("alltoken", out var metadata) ? metadata : default;

    private static bool IsAllTokenImageTerminal(string? status)
        => status is not null && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase));

    private static void EnsureAllTokenImageSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AllToken image {operation} failed ({(int)response.StatusCode}): {raw}");
    }

    private static string? ReadAllTokenString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime ReadAllTokenImageTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("created", out var created) && created.TryGetInt64(out var unix)) return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        if (DateTime.TryParse(ReadAllTokenString(root, "created_at"), out var timestamp)) return timestamp.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static OpenAIImageUsage? ToAllTokenOpenAIUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;
        return new OpenAIImageUsage { InputTokens = Number(usage, "input_tokens"), OutputTokens = Number(usage, "output_tokens"), TotalTokens = Number(usage, "total_tokens") };
    }

    private static ImageUsageData? ToAllTokenImageUsage(JsonElement root)
    {
        var usage = ToAllTokenOpenAIUsage(root);
        return usage is null ? null : new ImageUsageData { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens };
    }

    private static string RemoveAllTokenDataUrlPrefix(string value)
    {
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? value : value[(marker + 8)..];
    }

    private static string AllTokenImageMediaType(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", _ => "image/png"
    };

    private sealed record AllTokenImageTask(JsonElement Root, Dictionary<string, string> Headers);
    private sealed record AllTokenUpload(string UploadId);
    private sealed record AllTokenImage(string Base64, string MediaType, string? RevisedPrompt);
}
