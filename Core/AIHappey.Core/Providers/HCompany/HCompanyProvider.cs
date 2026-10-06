using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HCompany;

public partial class HCompanyProvider : IModelProvider, IUnifiedModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly AsyncCacheHelper _memoryCache;
    private readonly HttpClient _models;
    private readonly HttpClient _agents;
    private readonly IProviderDebugEmitter _debug;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public HCompanyProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory, IProviderDebugEmitter? debug = null)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _debug = debug ?? NullProviderDebugEmitter.Instance;
        _models = httpClientFactory.CreateClient("hcompany-models");
        _models.BaseAddress = new Uri("https://api.hcompany.ai/");
        _agents = httpClientFactory.CreateClient("hcompany-agents");
        _agents.BaseAddress = new Uri("https://agp.eu.hcompany.ai/api/v2/");
    }

    public string GetIdentifier() => "hcompany";

    private string RequireKey() => _keyResolver.Resolve(GetIdentifier()) is { Length: > 0 } key
        && !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidOperationException("No HCompany API key.");

    private static string LocalModel(string model) => model.StartsWith("hcompany/", StringComparison.OrdinalIgnoreCase)
        ? model[9..] : model;

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (TryAgent(options.Model, out _))
            return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();
        var prepared = PrepareHolo(options);
        _models.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        return await this.GetChatCompletion(_models, prepared, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (TryAgent(options.Model, out _))
        {
            await foreach (var evt in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
                yield return evt.ToChatCompletionUpdate();
            yield break;
        }
        var prepared = PrepareHolo(options);
        _models.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequireKey());
        await foreach (var update in this.GetChatCompletions(_models, prepared, cancellationToken: cancellationToken))
            yield return update;
    }

    private ChatCompletionOptions PrepareHolo(ChatCompletionOptions original)
    {
        var options = JsonSerializer.SerializeToElement(original, Json).Deserialize<ChatCompletionOptions>(Json)!;
        options.Headers = original.Headers;
        options.Model = LocalModel(options.Model);
        var provider = ProviderOptions(options.Metadata);
        var format = provider.TryGetValue("response_format", out var supplied) ? supplied
            : options.ResponseFormat is null ? default : Element(options.ResponseFormat);
        var schema = AnswerSchema(format);
        if (schema.HasValue && !provider.ContainsKey("structured_outputs"))
            provider["structured_outputs"] = Element(new { json = schema.Value });
        if (provider.ContainsKey("structured_outputs") &&
            (options.Tools?.Any() == true || provider.ContainsKey("tools")))
            throw new ArgumentException("Holo structured_outputs cannot be combined with native function tools.");
        if (schema.HasValue)
        {
            options.ResponseFormat = null;
            provider.Remove("response_format");
            options.AdditionalProperties?.Remove("response_format");
        }
        options.Metadata ??= [];
        options.Metadata[GetIdentifier()] = Element(provider);
        return options;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => TryAgent(request.Model, out _) ? ExecuteAgentAsync(request, cancellationToken)
            : this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => TryAgent(request.Model, out _) ? StreamAgentAsync(request, cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken)) yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken)) yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var ui in part.Event.ToUIMessagePart(GetIdentifier())) yield return ui;
    }

    private static JsonElement Element(object? value) => value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var child) ? child.Clone() : null;
    private static string? Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static int Int(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result) ? result : 0;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private Dictionary<string, JsonElement> ProviderOptions(Dictionary<string, object?>? metadata)
        => metadata?.TryGetValue(GetIdentifier(), out var value) == true && Element(value).ValueKind == JsonValueKind.Object
            ? Element(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
    private static JsonElement? AnswerSchema(JsonElement format)
    {
        if (Text(format, "type") != "json_schema") return null;
        return Property(Property(format, "json_schema") ?? format, "schema");
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
