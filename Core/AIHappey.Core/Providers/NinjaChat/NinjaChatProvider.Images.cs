using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    private const string NinjaChatImageEndpoint = "v1/images/generations";

    private static readonly JsonSerializerOptions NinjaChatMediaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.N is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(request), "NinjaChat image count must be between 1 and 4.");

        var warnings = new List<object>();
        var files = request.Files?.Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.Data)).ToList() ?? [];
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "multiple_input_images", details = "NinjaChat accepts one image URI; only the first image was sent." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "NinjaChat does not document mask-based image editing." });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = CopyNinjaChatProviderOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["prompt"] = request.Prompt;
        payload["model"] = request.Model;
        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;
        if (request.N is not null)
            payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (files.Count > 0)
            payload["image"] = ToNinjaChatImageUri(files[0]);

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NinjaChatImageEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NinjaChatMediaJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureNinjaChatMediaSuccess(response, raw, "image generation");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ReadNinjaChatImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("NinjaChat image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadNinjaChatCreated(root),
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateNinjaChatImageEdit(options);
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateNinjaChatImageEdit(options);
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await ImageRequest(request, cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private static void ValidateNinjaChatImageEdit(OpenAIImageEditRequest options)
    {
        options.ValidateOpenAIImageEditRequest();
        if (options.Mask is not null || options.MaskFile is not null)
            throw new NotSupportedException("NinjaChat does not document mask-based image editing.");

        var imageCount = (options.Images?.Length ?? 0) + (options.ImageFiles?.Length ?? 0);
        if (imageCount != 1)
            throw new NotSupportedException("NinjaChat image editing supports exactly one input image.");
    }

    private async Task<List<string>> ReadNinjaChatImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var base64Element) && base64Element.ValueKind == JsonValueKind.String)
            {
                var base64 = base64Element.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                    images.Add(base64.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            var url = ReadNinjaChatString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }

            var media = await DownloadNinjaChatMediaAsync(url, MediaTypeNames.Image.Png, cancellationToken);
            images.Add(Convert.ToBase64String(media.Bytes).ToDataUrl(media.MediaType));
        }

        return images;
    }

    private static string ToNinjaChatImageUri(ImageFile file)
    {
        var data = file.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data;

        return data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
    }

    private static DateTime ReadNinjaChatCreated(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : DateTime.UtcNow;
}
