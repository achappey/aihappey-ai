using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Creora;

public sealed partial class CreoraProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper memoryCache) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);
    private static readonly JsonSerializerOptions StandardJson = CreateStandardJson();

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://creora.ai/api/v1/");
        return client;
    }

    public string GetIdentifier() => "creora";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Creora API key.");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static JsonSerializerOptions CreateStandardJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ChatCompletionOptions) && info.Type != typeof(OpenAIImageGenerationRequest))
                return;
            foreach (var property in info.Properties.Where(property => property.IsExtensionData))
                property.ShouldSerialize = (_, _) => false;
        });
        return new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = resolver
        };
    }

    private Dictionary<string, object?> CopyOptions(Dictionary<string, JsonElement>? options)
        => options?.TryGetValue(GetIdentifier(), out var scoped) == true
            ? CopyObject(scoped)
            : new(StringComparer.Ordinal);

    private static Dictionary<string, object?> CopyObject(JsonElement options)
        => options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? new(StringComparer.Ordinal)
            : options.EnumerateObject().ToDictionary(
                property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);

    private static void ApplyStandardValues<T>(Dictionary<string, object?> payload, T options)
    {
        // Serialize extension data separately so explicit standard fields win collisions.
        foreach (var property in JsonSerializer.SerializeToElement(options, StandardJson).EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.Null)
                payload[property.Name] = property.Value.Clone();
    }

    private JsonElement ChatPayload(ChatCompletionOptions options, bool stream)
    {
        var metadata = options.GetProviderMetadata();
        var payload = metadata.TryGetValue(GetIdentifier(), out var raw)
            ? CopyObject(raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw))
            : new(StringComparer.Ordinal);
        foreach (var property in options.AdditionalProperties ?? [])
            if (property.Key != "providerMetadata")
                payload[property.Key] = property.Value.Clone();
        var hasRawTools = payload.TryGetValue("tools", out var rawTools);
        ApplyStandardValues(payload, options);
        if (options.Tools?.Any() != true)
        {
            if (hasRawTools)
                payload["tools"] = rawTools;
            else
                payload.Remove("tools");
        }
        payload["model"] = NativeModel(options.Model);
        payload["stream"] = stream;
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string NativeModel(string model)
        => model.StartsWith("creora/", StringComparison.Ordinal) ? model["creora/".Length..] : model;

    private HttpRequestMessage ChatMessage(ChatCompletionOptions options, bool stream)
    {
        ApplyAuthHeader();
        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(ChatPayload(options, stream))
        };
        request.Headers.Accept.Add(new(stream ? "text/event-stream" : "application/json"));
        foreach (var header in options.Headers ?? [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        using var request = ChatMessage(options, false);
        using var response = await _client.SendAsync(request, cancellationToken);
        var native = await ReadJsonAsync(response, cancellationToken);
        var completion = native.Root.ReadProviderChatCompletion();
        completion.Model = completion.Model.ToModelId(GetIdentifier());
        (completion.AdditionalProperties ??= [])["providerMetadata"] =
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [GetIdentifier()] = new { headers = native.Headers, response = native.Root }
            });
        return completion;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = ChatMessage(options, true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            EnsureSuccess(response, await response.Content.ReadAsStringAsync(cancellationToken));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = item.Data;
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                yield break;
            if (string.IsNullOrEmpty(data))
                continue;
            using var document = JsonDocument.Parse(data);
            if (item.EventType == "error")
                throw new InvalidOperationException($"Creora stream error: {data}");
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Creora stream error: {error.GetRawText()}");
            var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data)
                ?? throw new InvalidOperationException("Creora returned an empty chat chunk.");
            if (!string.IsNullOrEmpty(update.Model))
                update.Model = update.Model.ToModelId(GetIdentifier());
            yield return update;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => (await this.GetModel(request.Model, cancellationToken)).Type switch
        {
            "image" => await ExecuteImageAsync(request, cancellationToken),
            "video" => await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            _ => (await CompleteChatAsync(UnifiedChatOptions(request), cancellationToken)).ToUnifiedResponse(GetIdentifier())
        };

    private ChatCompletionOptions UnifiedChatOptions(AIRequest request)
    {
        // Keep native options out of typed Unified mappings and their synthetic defaults.
        var standard = new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input,
            Temperature = request.Temperature, TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls, Stream = request.Stream, ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice, ResponseFormat = request.ResponseFormat, Tools = request.Tools,
            Metadata = request.Metadata?.Where(entry => entry.Key != GetIdentifier()).ToDictionary(),
            Headers = request.Headers, Verbosity = request.Verbosity
        };
        var options = standard.ToChatCompletionOptions(GetIdentifier());
        options.Store = null;
        options.StreamOptions = null;
        options.TopP = request.TopP;
        options.MaxCompletionTokens = request.MaxOutputTokens;
        options.Verbosity = request.Verbosity;
        if (request.ToolChoice is null)
            options.ToolChoiceOption = null;
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var raw) == true)
            options.SetProviderMetadata(new() { [GetIdentifier()] = raw });
        return options;
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var type = (await this.GetModel(request.Model, cancellationToken)).Type;
        // The shared image streaming adapter builds a reduced OpenAI request and loses raw options.
        var stream = type switch
        {
            "image" => StreamImageAsync(request, cancellationToken),
            "video" => this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            _ => this.StreamUnifiedViaChatCompletionsAsync(request, UnifiedChatOptions(request), cancellationToken: cancellationToken)
        };
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in item.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return item;
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Creora has no native Responses API.");
    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Creora has no native Responses API.");
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Creora does not document image editing.");
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Creora does not document image editing.");
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
