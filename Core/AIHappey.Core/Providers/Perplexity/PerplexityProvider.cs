using AIHappey.Core.AI;
using AIHappey.Messages;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using AIHappey.Messages.Mapping;
using System.Text.Json;
using AIHappey.Core.Contracts;
using System.Globalization;
using AIHappey.Unified.Models;
using AIHappey.Sampling.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider : IModelProvider
{
    private readonly string BASE_URL = "https://api.perplexity.ai/";

    public string GetIdentifier() => nameof(Perplexity).ToLowerInvariant();

    private readonly IApiKeyResolver _keyResolver;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public PerplexityProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _httpClientFactory = httpClientFactory;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(BASE_URL);
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Perplexity)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
          cancellationToken);

        return result.ToSamplingResult();
    }

    //  public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    //         => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var (requestOptions, extraRootProperties) = PrepareChatCompletionRequest(options);

        var result = await this.GetChatCompletion(_client,
                    requestOptions,
                    relativeUrl: "v1/sonar",
                    cancellationToken: cancellationToken,
                    extraRootProperties: extraRootProperties);

        return await EnrichChatCompletionImagesAsync(result, cancellationToken);
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
     ChatCompletionOptions options,
     [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var (requestOptions, extraRootProperties) = PrepareChatCompletionRequest(options);
        var downloadedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var update in this.GetChatCompletions(_client,
            requestOptions,
            relativeUrl: "v1/sonar",
            extraRootProperties: extraRootProperties,
            cancellationToken: cancellationToken))
        {
            if (TryGetImagesFromAdditionalProperties(update.AdditionalProperties, out var imagesElement))
            {
                var newImages = new List<PerplexityDownloadedImage>();

                foreach (var imageElement in imagesElement.EnumerateArray())
                {
                    var url = ExtractImageUrl(imageElement);
                    if (string.IsNullOrWhiteSpace(url) || !downloadedUrls.Add(url))
                        continue;

                    var downloaded = await TryDownloadPerplexityImageAsync(imageElement, cancellationToken);
                    if (downloaded is not null)
                        newImages.Add(downloaded);
                }

                if (newImages.Count > 0)
                {
                    update.AdditionalProperties ??= new(StringComparer.OrdinalIgnoreCase);
                    update.AdditionalProperties["downloaded_images"] =
                        BuildDownloadedImagesElement(newImages);
                }
            }

            yield return update;
        }
    }

    private (ChatCompletionOptions Request, JsonElement? ExtraRootProperties) PrepareChatCompletionRequest(ChatCompletionOptions options)
    {
        var extra = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var additionalProperties = new Dictionary<string, JsonElement>();

        if (TryGetProviderMetadataElement(options.Metadata, out var providerMetadata))
        {
            foreach (var prop in providerMetadata.EnumerateObject())
            {
                if (!extra.ContainsKey(prop.Name))
                    extra[prop.Name] = prop.Value.Clone();
            }
        }

        additionalProperties = extra.Count > 0 ? extra : null;
        options.Metadata = null;

        JsonElement? extraRoot = additionalProperties is { Count: > 0 }
            ? JsonSerializer.SerializeToElement(additionalProperties, JsonSerializerOptions.Web)
            : null;

        return (options, extraRoot);
    }

    private bool TryGetProviderMetadataElement(Dictionary<string, object?>? metadata, out JsonElement providerMetadata)
    {
        providerMetadata = default;

        if (metadata is null || !metadata.TryGetValue(GetIdentifier(), out var value) || value is null)
            return false;

        providerMetadata = value switch
        {
            JsonElement je => je,
            _ => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web)
        };

        return providerMetadata.ValueKind == JsonValueKind.Object;
    }

    private static bool UsesResponsesPreset(string? model)
        => string.Equals(model, "fast-search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "pro-search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "deep-research", StringComparison.OrdinalIgnoreCase);


    private static decimal? TryGetPerplexityTotalCost(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetProperty(usage, "cost", out var costElement) || costElement.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetProperty(costElement, "total_cost", out var totalCostElement))
            return null;

        return totalCostElement.ValueKind switch
        {
            JsonValueKind.Number when totalCostElement.TryGetDecimal(out var totalCost) => totalCost,
            JsonValueKind.String when decimal.TryParse(totalCostElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }



    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }

        yield break;
    }


    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Model?.StartsWith($"sonar") != true)
        {
            return this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
        }

        return this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }


    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Model?.StartsWith($"sonar") != true)
        {
            return this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
        }

        return this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    private async Task<ChatCompletion> EnrichChatCompletionImagesAsync(
        ChatCompletion response,
        CancellationToken cancellationToken)
    {
        if (!TryGetImagesFromAdditionalProperties(response.AdditionalProperties, out var imagesElement))
            return response;

        var downloaded = await DownloadPerplexityImagesAsync(imagesElement, cancellationToken);
        if (downloaded.Count == 0)
            return response;

        response.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        response.AdditionalProperties["downloaded_images"] = BuildDownloadedImagesElement(downloaded);
        return response;
    }

    private async Task<ChatCompletionUpdate> EnrichChatCompletionUpdateImagesAsync(
        ChatCompletionUpdate update,
        CancellationToken cancellationToken)
    {
        if (!TryGetImagesFromAdditionalProperties(update.AdditionalProperties, out var imagesElement))
            return update;

        var downloaded = await DownloadPerplexityImagesAsync(imagesElement, cancellationToken);
        if (downloaded.Count == 0)
            return update;

        update.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        update.AdditionalProperties["downloaded_images"] = BuildDownloadedImagesElement(downloaded);
        return update;
    }

    private async Task<List<PerplexityDownloadedImage>> DownloadPerplexityImagesAsync(
        JsonElement imagesElement,
        CancellationToken cancellationToken)
    {
        var list = new List<PerplexityDownloadedImage>();

        if (imagesElement.ValueKind != JsonValueKind.Array)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var imageElement in imagesElement.EnumerateArray())
        {
            var sourceUrl = ExtractImageUrl(imageElement);
            if (string.IsNullOrWhiteSpace(sourceUrl) || !seen.Add(sourceUrl))
                continue;

            var downloaded = await TryDownloadPerplexityImageAsync(imageElement, cancellationToken);
            if (downloaded is not null)
                list.Add(downloaded);
        }

        return list;
    }

    private async Task<PerplexityDownloadedImage?> TryDownloadPerplexityImageAsync(
        JsonElement imageElement,
        CancellationToken cancellationToken)
    {
        var imageUrl = ExtractImageUrl(imageElement);
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType
                            ?? GuessImageMediaType(imageUrl)
                            ?? "image/png";

            var base64 = Convert.ToBase64String(bytes);
            var dataUrl = $"data:{mediaType};base64,{base64}";

            var title = imageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(imageElement, "title")
                : null;

            var originUrl = imageElement.ValueKind == JsonValueKind.Object
                ? TryGetString(imageElement, "origin_url")
                : null;

            var filename = BuildImageFilename(title, mediaType);

            return new PerplexityDownloadedImage
            {
                DataUrl = dataUrl,
                MediaType = mediaType,
                Filename = filename,
                OriginUrl = originUrl,
                Title = title,
                Width = imageElement.ValueKind == JsonValueKind.Object ? TryGetInt32(imageElement, "width") : null,
                Height = imageElement.ValueKind == JsonValueKind.Object ? TryGetInt32(imageElement, "height") : null
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement BuildDownloadedImagesElement(List<PerplexityDownloadedImage> images)
    {
        var payload = images.Select(image => new Dictionary<string, object?>
        {
            ["data_url"] = image.DataUrl,
            ["media_type"] = image.MediaType,
            ["filename"] = image.Filename,
            ["origin_url"] = image.OriginUrl,
            ["title"] = image.Title,
            ["width"] = image.Width,
            ["height"] = image.Height
        }).ToList();

        return JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
    }

    private static bool TryGetImagesFromAdditionalProperties(
        Dictionary<string, JsonElement>? additionalProperties,
        out JsonElement imagesElement)
    {
        if (additionalProperties is not null
            && additionalProperties.TryGetValue("images", out imagesElement)
            && imagesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        imagesElement = default;
        return false;
    }

    private static string? ExtractImageUrl(JsonElement imageElement)
    {
        if (imageElement.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(imageElement, "image_url")
                ?? TryGetString(imageElement, "url");
        }

        if (imageElement.ValueKind == JsonValueKind.String)
            return imageElement.GetString();

        return null;
    }

    private static string BuildImageFilename(string? title, string mediaType)
    {
        var ext = mediaType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/jpg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            _ => "bin"
        };

        var safeTitle = string.IsNullOrWhiteSpace(title)
            ? "perplexity-image"
            : string.Concat(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');

        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = "perplexity-image";

        return $"{safeTitle}.{ext}";
    }

    private static string? GuessImageMediaType(string url)
    {
        var lower = url.ToLowerInvariant();

        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".gif")) return "image/gif";
        if (lower.Contains(".svg")) return "image/svg+xml";

        return null;
    }

    private sealed class PerplexityDownloadedImage
    {
        public required string DataUrl { get; init; }
        public required string MediaType { get; init; }
        public required string Filename { get; init; }
        public string? OriginUrl { get; init; }
        public string? Title { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }

    }
}

