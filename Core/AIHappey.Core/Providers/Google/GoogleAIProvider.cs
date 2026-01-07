using AIHappey.Common.Model;
using OAIC = OpenAI.Chat;
using Microsoft.Extensions.Logging;
using AIHappey.Core.AI;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider(IApiKeyResolver keyResolver, ILogger<GoogleAIProvider> logger)
    : IModelProvider
{
    private readonly string FILES_API = "https://generativelanguage.googleapis.com/v1beta/files";

    private Mscc.GenerativeAI.GoogleAI GetClient()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        return new(key, logger: logger);
    }

    private static readonly string Google = "Google";

    

    public async Task<UIMessagePart> CompleteAsync(ChatCompletionOptions request, CancellationToken cancellationToken = default)
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

    public string GetIdentifier() => GoogleExtensions.Identifier();

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
