using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.Models;
using OpenAI.Responses;
using OAIC = OpenAI.Chat;

namespace AIHappey.Core.AI;

public interface IModelProvider
{
    string GetIdentifier();
    float? GetPriority();

    Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default);

    Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default);

    IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default);

    Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default);

    Task<ModelContextProtocol.Protocol.CreateMessageResult> SamplingAsync(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default);

    IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default);

    Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default);

    Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default);

    Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default);

}

public interface IApiKeyResolver
{
    string? Resolve(string provider);
}
