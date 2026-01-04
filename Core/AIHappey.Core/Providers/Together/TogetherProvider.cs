using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public TogetherProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.together.xyz/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Together)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public float? GetPriority() => 1;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => "together";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Together API error: {err}");
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
            //  if (!el.TryGetProperty("type", out var typeEl) ||
            //        typeEl.GetString() != "chat")
            //    continue;

            Model model = new();

            if (el.TryGetProperty("id", out var idEl))
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";

            if (el.TryGetProperty("display_name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("context_length", out var contextLengthEl))
                model.ContextWindow = contextLengthEl.GetInt32();

            if (el.TryGetProperty("type", out var typeEl) && !string.IsNullOrEmpty(typeEl.GetString()))
                model.Type = typeEl.GetString()!;

            if (model.Type == "chat")
                model.Type = "language";

            if (model.Type == "transcribe")
                model.Type = "transcription";

            if (model.Type == "audio")
                model.Type = "speech";

            if (model.Type == "rerank")
                model.Type = "reranking";

            if (el.TryGetProperty("organization", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("pricing", out var pricingEl) &&
                pricingEl.ValueKind == JsonValueKind.Object)
            {
                var inputPrice = pricingEl.TryGetProperty("input", out var inEl)
                        ? inEl.GetRawText() : null;

                var outputPrice = pricingEl.TryGetProperty("output", out var outEl)
                        ? outEl.GetRawText() : null;

                if (!string.IsNullOrEmpty(outputPrice)
                    && !string.IsNullOrEmpty(inputPrice)
                    && !outputPrice.Equals("0")
                    && !inputPrice.Equals("0"))
                    model.Pricing = new ModelPricing
                    {
                        Input = inputPrice,
                        Output = outputPrice
                    };
            }

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
            {
                var unix = createdEl.GetInt64();
                model.Created = unix;
            }

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        /* if (!models.Any(a => a.Id.EndsWith("mistralai/Voxtral-Mini-3B-2507")))
             models.Add(new()
             {
                 Id = "mistralai/Voxtral-Mini-3B-2507".ToModelId(GetIdentifier()),
                 Name = "Voxtral Mini 3B",
                 OwnedBy = "Mistral",
                 Type = "transcription"
             });*/

        return models.Where(a => a.Type != "moderation"
            && a.Type != "code");
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}