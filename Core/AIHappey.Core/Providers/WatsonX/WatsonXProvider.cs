using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Providers.WatsonX;

public sealed class WatsonXProvider : IModelProvider
{
    private const string IamTokenEndpoint = "https://iam.cloud.ibm.com/identity/token";
    private const string ApiVersion = "2024-05-31";
    private const string ChatEndpoint = "ml/v1/text/chat?version=" + ApiVersion;
    private const string ChatStreamEndpoint = "ml/v1/text/chat_stream?version=" + ApiVersion;
    private const string ModelsEndpoint =
        "ml/v1/foundation_model_specs?version=" + ApiVersion + "&filters=function_text_chat&limit=200";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly HttpClient _iamClient;
    private readonly AsyncCacheHelper _cache;

    public WatsonXProvider(
        IApiKeyResolver keyResolver,
        IOptions<WatsonXProviderOptions> options,
        AsyncCacheHelper cache,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _cache = cache;
        _client = httpClientFactory.CreateClient();
        _iamClient = httpClientFactory.CreateClient();

        if (!string.IsNullOrWhiteSpace(options.Value.Endpoint))
        {
            if (!Uri.TryCreate(options.Value.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new ArgumentException("WatsonX endpoint must be an absolute HTTPS URL.", nameof(options));
            }

            _client.BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/");
        }
    }

    public string GetIdentifier() => "watsonx";

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        await ApplyAuthHeaderAsync(cancellationToken);
        var headers = GetRequestHeaders(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
        {
            Content = new StringContent(BuildNativeRequest(options).GetRawText(), Encoding.UTF8, "application/json")
        };
        ApplyRequestHeaders(request, headers);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfNotSuccess(response, body);

        var normalizedBody = NormalizeResponseModel(body, options.Model);
        return JsonSerializer.Deserialize<ChatCompletion>(normalizedBody, JsonOptions)
            ?? throw new InvalidOperationException("watsonx returned an empty chat response.");
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ApplyAuthHeaderAsync(cancellationToken);
        var headers = GetRequestHeaders(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatStreamEndpoint)
        {
            Content = new StringContent(BuildNativeRequest(options).GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyRequestHeaders(request, headers);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var errorBody = response.IsSuccessStatusCode
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfNotSuccess(response, errorBody);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested
               && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 || line.StartsWith(':'))
            {
                if (dataLines.Count > 0)
                {
                    var data = string.Join("\n", dataLines);
                    dataLines.Clear();
                    if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                        yield break;

                    yield return ParseStreamUpdate(data, options.Model);
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].Trim());
        }

        if (!cancellationToken.IsCancellationRequested && dataLines.Count > 0)
        {
            var data = string.Join("\n", dataLines);
            if (!string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                yield return ParseStreamUpdate(data, options.Model);
        }
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey) || _client.BaseAddress is null)
            return [];

        var cacheKey = $"{this.GetCacheKey(apiKey)}:{_client.BaseAddress}";
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var accessToken = await GetAccessTokenAsync(apiKey, ct);
                var models = new List<Model>();
                var nextUrl = ModelsEndpoint;
                var visitedUrls = new HashSet<string>(StringComparer.Ordinal);

                while (visitedUrls.Add(nextUrl))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    using var response = await _client.SendAsync(request, ct);
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"watsonx model discovery failed (HTTP {(int)response.StatusCode}): {body}",
                            null,
                            response.StatusCode);
                    }

                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("resources", out var resources)
                        && resources.ValueKind == JsonValueKind.Array)
                    {
                        models.AddRange(resources.EnumerateArray()
                            .Select(ToModel)
                            .OfType<Model>());
                    }

                    if (!document.RootElement.TryGetProperty("next", out var next)
                        || next.ValueKind != JsonValueKind.Object
                        || !next.TryGetProperty("href", out var href)
                        || href.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(href.GetString()))
                    {
                        break;
                    }

                    var nextUri = new Uri(_client.BaseAddress!, href.GetString()!);
                    if (nextUri.Scheme != _client.BaseAddress!.Scheme
                        || !nextUri.Host.Equals(_client.BaseAddress.Host, StringComparison.OrdinalIgnoreCase)
                        || nextUri.Port != _client.BaseAddress.Port)
                    {
                        throw new InvalidOperationException("watsonx model discovery returned a page outside the configured endpoint.");
                    }

                    nextUrl = nextUri.PathAndQuery;
                }

                return models
                    .DistinctBy(model => model.Id)
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return response.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).ToResponseStreamParts(cancellationToken))
        {
            yield return part;
        }
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return response.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).ToMessageStreamParts(request.Model, cancellationToken))
        {
            yield return part;
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    private async Task ApplyAuthHeaderAsync(CancellationToken cancellationToken)
    {
        EnsureEndpoint();
        var apiKey = GetApiKey();
        var accessToken = await GetAccessTokenAsync(apiKey, cancellationToken);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private async Task<string> GetAccessTokenAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, IamTokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ibm:params:oauth:grant-type:apikey",
                ["apikey"] = apiKey
            })
        };

        using var response = await _iamClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"IBM IAM token exchange failed (HTTP {(int)response.StatusCode}): {body}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new InvalidOperationException("IBM IAM token response did not include an access token.");
        }

        return token.GetString()!;
    }

    private string GetApiKey()
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No watsonx API key.");

        return apiKey;
    }

    private void EnsureEndpoint()
    {
        if (_client.BaseAddress is null)
            throw new InvalidOperationException("Configure the watsonx regional endpoint before making requests.");
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("model_id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return null;
        }

        var id = idElement.GetString()!;
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "label") ?? id,
            OwnedBy = GetString(item, "provider") ?? GetIdentifier(),
            Description = GetString(item, "short_description") ?? GetString(item, "long_description"),
            Type = "language"
        };
    }

    private JsonElement BuildNativeRequest(ChatCompletionOptions options)
    {
        var serializedOptions = JsonSerializer.SerializeToElement(options, JsonOptions);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serializedOptions.EnumerateObject())
            properties[property.Name] = property.Value.Clone();

        if (options.Metadata?.TryGetValue(GetIdentifier(), out var providerOptions) == true)
        {
            var providerOptionsElement = providerOptions switch
            {
                null => default,
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(providerOptions, JsonOptions)
            };

            if (providerOptionsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in providerOptionsElement.EnumerateObject())
                {
                    if (ProviderMetadataExcludedProperties.Contains(property.Name))
                        continue;

                    properties[property.Name] = property.Value.Clone();
                }
            }
        }

        properties.Remove("model");
        properties.Remove("model_id");
        properties.Remove("stream");
        properties.Remove("store");
        properties.Remove("stream_options");
        properties.Remove("metadata");
        properties.Remove("providerMetadata");
        properties["model_id"] = JsonSerializer.SerializeToElement(StripProviderPrefix(options.Model), JsonOptions);

        return JsonSerializer.SerializeToElement(properties, JsonOptions);
    }

    private string NormalizeResponseModel(string body, string requestedModel)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("watsonx returned a non-object chat response.");

        var modelId = GetString(document.RootElement, "model_id") ?? StripProviderPrefix(requestedModel);
        var response = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("model_id"))
                continue;

            response[property.Name] = property.Value.Clone();
        }

        response["model"] = JsonSerializer.SerializeToElement(PrefixModel(modelId), JsonOptions);
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private ChatCompletionUpdate ParseStreamUpdate(string data, string requestedModel)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("watsonx returned a non-object streaming event.");

            var modelId = GetString(document.RootElement, "model_id") ?? StripProviderPrefix(requestedModel);
            var update = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("model_id"))
                    continue;

                update[property.Name] = property.Value.Clone();
            }

            update["model"] = JsonSerializer.SerializeToElement(PrefixModel(modelId), JsonOptions);
            return JsonSerializer.Deserialize<ChatCompletionUpdate>(
                       JsonSerializer.Serialize(update, JsonOptions),
                       JsonOptions)
                   ?? throw new InvalidOperationException("watsonx returned an empty streaming event.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse watsonx streaming event: {data}", ex);
        }
    }

    private IReadOnlyDictionary<string, string>? GetRequestHeaders(ChatCompletionOptions options)
    {
        var headers = this.SetDefaultChatCompletionProperties(options);
        if ((headers is null || headers.Count == 0) && (options.Headers is null || options.Headers.Count == 0))
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                result[name] = value;
        }

        if (options.Headers is not null)
        {
            foreach (var (name, value) in options.Headers)
                result[name] = value;
        }

        return result;
    }

    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            else
                request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response, string? body)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new HttpRequestException(
            $"watsonx chat request failed (HTTP {(int)response.StatusCode}): {body}",
            null,
            response.StatusCode);
    }

    private static string? GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string StripProviderPrefix(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private string PrefixModel(string model)
        => model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{GetIdentifier()}/{model}";

    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "model_id",
        "label",
        "provider",
        "short_description",
        "long_description",
        "model_version",
        "source"
    };

    private static readonly HashSet<string> ProviderMetadataExcludedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "headers",
        "tools",
        "providerMetadata"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

public sealed class WatsonXProviderOptions
{
    public string? Endpoint { get; set; }
}
