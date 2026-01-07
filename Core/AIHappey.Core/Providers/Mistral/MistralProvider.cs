using AIHappey.Core.AI;
using AIHappey.Core.Models;
using OAIC = OpenAI.Chat;
using MIS = Mistral.SDK;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly IApiKeyResolver _keyResolver;

    public MistralProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.mistral.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Mistral)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => "mistral";

    private string GetName() => nameof(Mistral);

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var client = new MIS.MistralClient(
          _keyResolver.Resolve(GetIdentifier()),
          _client
        );

        var models = await client.Models
            .GetModelsAsync(cancellationToken: cancellationToken);

        List<Model> imageModels = [new Model()
            {
                Id = "mistral-medium-latest".ToModelId(GetIdentifier()),
                Name = "mistral-medium-latest",
                OwnedBy = GetName(),
                Type = "image"
            }, new Model()
            {
                Id = "mistral-large-latest".ToModelId(GetIdentifier()),
                Name = "mistral-large-latest",
                OwnedBy = GetName(),
                Type = "image"
            }];

        return models.Data
            .Select(a => new Model()
            {
                Id = a.Id.ToModelId(GetIdentifier()),
                Name = a.Id,
                OwnedBy = GetName(),
            })
            .Concat(imageModels)
            .OrderByDescending(a => a.Created);
    }

    public Task<OAIC.ChatCompletion> CompleteChatAsync(OAIC.ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(OAIC.ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}