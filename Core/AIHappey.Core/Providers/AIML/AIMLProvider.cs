using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;
using AIHappey.Common.Model.Responses;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public AIMLProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.aimlapi.com/");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(AIML)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"AI/ML API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var model = new Model
            {
                Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? ""
            };

            if (string.IsNullOrEmpty(model.Id))
                continue;

            // type
            if (el.TryGetProperty("type", out var typeEl))
            {
                var type = typeEl.GetString();
                model.Type = type == "chat-completion" ? "language" :
                    type == "responses" ? "language" :
                    type == "tts" ? "speech" :
                    type == "stt" ? "transcription"
                    : model.Id.Contains("music")
                    ? "speech" : type ?? "";
            }

            // info block
            if (el.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
            {
                if (infoEl.TryGetProperty("name", out var nameEl))
                    model.Name = nameEl.GetString() ?? model.Id;

                if (infoEl.TryGetProperty("contextLength", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
                    model.ContextWindow = ctxEl.GetInt32();

                if (infoEl.TryGetProperty("developer", out var devEl))
                    model.OwnedBy = devEl.GetString() ?? "";
            }

            models.Add(model);
        }

        return models.Where(a => a.Type != "document"
            && a.Type != "language-completion");
    }



    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model.Type == "speech")
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => AIMLExtensions.GetIdentifier();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        switch (model?.Type)
        {
            case "speech":
                {
                    return await this.SpeechSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }

            case "image":
                {
                    return await this.ImageSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }


            default:
                throw new NotImplementedException();
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id == chatRequest.Model);

        switch (model?.Type)
        {
            case "image":
                {
                    await foreach (var update in this.StreamImageAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            case "speech":
                {
                    await foreach (var update in this.StreamSpeechAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            case "transcription":
                {
                    await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            default:
                {
                    await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }


        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

}