using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.TestInfrastructure;

internal sealed class FixtureResponseStreamModelProvider : IModelProvider
{
    private readonly IReadOnlyList<ResponseStreamPart> responseStreamParts;
    private readonly string providerId;

    public FixtureResponseStreamModelProvider(string providerId, IReadOnlyList<ResponseStreamPart> responseStreamParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(responseStreamParts);

        this.providerId = providerId;
        this.responseStreamParts = responseStreamParts;
    }

    public string GetIdentifier() => providerId;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var part in responseStreamParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
            await Task.Yield();
        }
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => throw CreateUnsupportedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
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
        => new("This fixture provider only supports [`ResponsesStreamingAsync()`](Core/AIHappey.Core/Contracts/IModelProvider.cs:21) replay for mapper tests.");
}
