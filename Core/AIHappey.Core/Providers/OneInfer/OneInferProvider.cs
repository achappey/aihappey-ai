using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Sampling.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public OneInferProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.oneinfer.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(OneInfer)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "v1/ula/chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "v1/ula/chat/completions",
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(OneInfer).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToSamplingResult();
    }


    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audioBytes = Convert.FromBase64String(audioString.RemoveDataUrlPrefix());
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var requestFields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = request.Model,
            ["file"] = new
            {
                fileName,
                mediaType = request.MediaType,
                bytes = audioBytes.LongLength
            }
        };

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        AddOneInferMultipartMetadata(form, metadata, requestFields, "file", "model");

        using var response = await _client.PostAsync("v1/ula/generate-audio", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer transcription failed ({(int)response.StatusCode}): {raw}");

        if (!TryParseOneInferJson(raw, out var document))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = raw
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, OneInferJsonOptions)
                }
            };
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            var data = OneInferGetData(root);
            var text = OneInferTryGetString(data, "text", "transcript") ?? string.Empty;
            var language = OneInferTryGetString(data, "language") ?? OneInferTryGetString(metadata, "language");

            return new TranscriptionResponse
            {
                Text = text,
                Language = language,
                DurationInSeconds = OneInferTryGetFloat(data, "duration", "durationInSeconds", "duration_seconds"),
                Segments = ParseOneInferTranscriptionSegments(data),
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new ResponseData
                {
                    Timestamp = ReadOneInferUnixTimestamp(data, "created") ?? now,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = root
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, OneInferJsonOptions)
                }
            };
        }
    }

  

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

 

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
      => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
}
