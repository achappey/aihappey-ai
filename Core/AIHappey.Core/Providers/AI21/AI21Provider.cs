using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AI21;

/// <summary>
/// AI21 Jamba API.
/// Base URL: https://api.ai21.com/studio/
/// - POST v1/chat/completions
/// </summary>
public sealed partial class AI21Provider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.ai21.com/studio/");
        return client;
    }

    public string GetIdentifier() => "ai21";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No AI21 API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var hasKey = !string.IsNullOrWhiteSpace(keyResolver.Resolve(GetIdentifier()));
        if (!hasKey)
            return Task.FromResult<IEnumerable<Model>>([]);

        return Task.FromResult<IEnumerable<Model>>(
        [
            new Model
            {
                OwnedBy = nameof(AI21),
                Name = "jamba-large",
                ContextWindow = 256_000,
                Description = "Our most powerful and advanced model, designed to handle complex tasks at enterprise scale with superior performance.",
                Type = "language",
                Id = "jamba-large".ToModelId(GetIdentifier()),
                Pricing = new() {
                    Input = 2.00m,
                    Output = 8.00m
                }
            },
            new Model
            {
                OwnedBy = nameof(AI21),
                Name = "jamba-mini",
                ContextWindow = 256_000,
                Type = "language",
                Description = "Jamba2 Mini blends efficiency and steerability into a 12B-active parameters model, delivering reliable output on core enterprise workflows.",
                Id = "jamba-mini".ToModelId(GetIdentifier()),
                Pricing = new() {
                    Input = 0.20m,
                    Output = 0.40m
                }
            }
        ]);
    }

    // Implemented in AI21Provider.Chat.cs

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}

