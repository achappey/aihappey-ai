using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider : IModelProvider
{
    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    public JinaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://deepsearch.jina.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Jina API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => "jina";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>([new Model() {
                    Id = "jina-deepsearch-v1".ToModelId(GetIdentifier()),
                    Name = "jina-deepsearch-v1",
                    OwnedBy = nameof(Jina),
                    Created = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)
                        .ToUnixTimeSeconds()

        }]);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public float? GetPriority() => 1;
}