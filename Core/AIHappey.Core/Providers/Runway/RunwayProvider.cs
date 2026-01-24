using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public RunwayProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.dev.runwayml.com/");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        _client.DefaultRequestHeaders.Add("X-Runway-Version", "2024-11-06");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Runway)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }



    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => "runway";

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
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

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
       ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model?.Type)
        {
            case "speech":
                {
                    await foreach (var update in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return update;

                    yield break;
                }

            case "image":
                {
                    await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken))
                        yield return update;

                    yield break;
                }

            default:
                throw new NotImplementedException();
        }
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => string.Equals(request?.Model, "eleven_text_to_sound_v2", StringComparison.OrdinalIgnoreCase)
            ? RunwaySoundEffectAsync(request!, cancellationToken)
            : RunwayTextToSpeechAsync(request!, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
