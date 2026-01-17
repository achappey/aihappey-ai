using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Responses;
using AIHappey.Common.Model.Responses.Streaming;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public interface IModelProvider
{
    string GetIdentifier();

    Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default);

    Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default);

    Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default);

    Task<ModelContextProtocol.Protocol.CreateMessageResult> SamplingAsync(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default);

    IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default);

    Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default);

    Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default);

    Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default);

    Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default);

    Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default);

}

public interface IApiKeyResolver
{
    string? Resolve(string provider);
}
