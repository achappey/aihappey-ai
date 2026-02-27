using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private async IAsyncEnumerable<UIMessagePart> StreamChatCoreAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(
                           chatRequest,
                           cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private async Task<ResponseResult> ResponsesCoreAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        return await _client.GetResponses(options, ct: cancellationToken);
    }

    private IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingCoreAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        return _client.GetResponsesUpdates(options, ct: cancellationToken);
    }
}

