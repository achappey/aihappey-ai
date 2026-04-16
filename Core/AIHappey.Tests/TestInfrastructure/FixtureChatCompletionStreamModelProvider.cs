using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.TestInfrastructure;

internal sealed class FixtureChatCompletionStreamModelProvider : IModelProvider
{
    private readonly IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates;
    private readonly string providerId;

    public FixtureChatCompletionStreamModelProvider(string providerId, IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(chatCompletionUpdates);

        this.providerId = providerId;
        this.chatCompletionUpdates = chatCompletionUpdates;
    }

    public string GetIdentifier() => providerId;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in chatCompletionUpdates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<ModelContextProtocol.Protocol.CreateMessageResult> SamplingAsync(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    private static NotSupportedException CreateUnsupportedException()
        => new("This fixture provider only supports [`CompleteChatStreamingAsync()`](Core/AIHappey.Core/Contracts/IModelProvider.cs:17) replay for mapper tests.");
}
