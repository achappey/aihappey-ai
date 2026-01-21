using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Echo;

/// <summary>
/// Debug provider that echoes the last user message back to the client.
/// </summary>
public sealed partial class EchoProvider() : IModelProvider
{
    public string GetIdentifier() => "echo";

    public IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
        => StreamEchoAsync(chatRequest, cancellationToken);

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                Id = "Echo".ToModelId(GetIdentifier()),
                Name = "Echo",
                OwnedBy = "Echo",
                Type = "language",
                Description = "Sends the last user message back."
            }
        ]);

    // ChatCompletions endpoint is not used by the Vercel UI stream (`/api/chat`).
    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

