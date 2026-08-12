using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        var isEdit = files.Count > 0;
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeEverypixelModel(request.Model),
            ["prompt"] = request.Prompt
        };

        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var imageSize = ResolveEverypixelImageSize(request.AspectRatio, request.Size);
        if (!string.IsNullOrWhiteSpace(imageSize)) payload["image_size"] = imageSize;

        if (isEdit)
            payload["image_urls"] = files.Select(NormalizeEverypixelImage).ToArray();

        CopyEverypixelProviderOptions(request.ProviderOptions, payload,
            "resolution", "style", "lora_url", "megapixel_ratio", "callback_url");

        var endpoint = isEdit ? "v1/image_edit" : "v1/image_generate";
        var requestBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} image {(isEdit ? "edit" : "generate")} failed ({(int)createResponse.StatusCode}): {createRaw}");

        var create = DeserializeOrThrow<EverypixelTaskStatusResponse>(createRaw, "image create response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{ProviderName} image response missing task_id: {createRaw}");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            ct => GetTaskStatusAsync(create.TaskId, ct), s => IsTerminalStatus(s.Status),
            TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(15), null, cancellationToken);
        if (!string.Equals(final.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} image task failed (task_id={create.TaskId}, status={final.Status}): {final.RawJson}");

        var urls = ExtractEverypixelResultUrls(final.Result, final.RawRoot);
        if (urls.Count == 0)
            throw new InvalidOperationException($"{ProviderName} image status returned no image URL: {final.RawJson}");

        var images = new List<string>();
        IDictionary<string, string>? headers = null;
        foreach (var url in urls)
        {
            using var download = await _client.GetAsync(url, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} image download failed ({(int)download.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
            var mediaType = download.Content.Headers.ContentType?.MediaType ?? GuessImageMediaType(url);
            images.Add($"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
            headers ??= download.GetHeaders();
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { task_id = create.TaskId, create = createRaw, status = final.RawJson, urls }),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var item in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var response = await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var response = await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken), cancellationToken);
        foreach (var item in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static string NormalizeEverypixelImage(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data)) throw new ArgumentException("Image data is required.", nameof(file));
        var value = file.Data.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
        if (string.IsNullOrWhiteSpace(file.MediaType)) throw new ArgumentException("Image mediaType is required for base64 data.", nameof(file));
        return $"data:{file.MediaType};base64,{value}";
    }

    private static string? ResolveEverypixelImageSize(string? aspectRatio, string? size)
    {
        var value = !string.IsNullOrWhiteSpace(aspectRatio) ? aspectRatio : size;
        return value?.Trim().ToLowerInvariant() switch
        {
            "1:1" or "1024x1024" => "square",
            "2:3" or "1024x1536" => "portrait_3_2",
            "3:4" => "portrait_4_3",
            "9:16" => "portrait_16_9",
            "3:2" or "1536x1024" => "landscape_3_2",
            "4:3" => "landscape_4_3",
            "16:9" => "landscape_16_9",
            null or "" => null,
            _ => value
        };
    }

    private static string GuessImageMediaType(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png"
    };
}
