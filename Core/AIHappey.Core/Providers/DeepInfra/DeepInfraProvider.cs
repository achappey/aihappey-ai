using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.DeepInfra;

/// <summary>
/// DeepInfra is OpenAI Chat Completions compatible.
/// Endpoint: POST https://api.deepinfra.com/v1/openai/chat/completions
/// Images endpoint: POST https://api.deepinfra.com/v1/openai/images/generations
/// </summary>
public sealed partial class DeepInfraProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.deepinfra.com/");
        return client;
    }

    public string GetIdentifier() => "deepinfra";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No DeepInfra API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    // ChatCompletions endpoint is not used by the Vercel UI stream (`/api/chat`).
    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ModelContextProtocol.Protocol.CreateMessageResult> SamplingAsync(
        ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
  
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

