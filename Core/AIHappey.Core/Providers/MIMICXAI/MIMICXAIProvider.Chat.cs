using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in ExecuteNativeUiStreamAsync(chatRequest, cancellationToken))
            yield return update;
    }

    private async IAsyncEnumerable<UIMessagePart> ExecuteNativeUiStreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        MimicXAiNativeTextRequest request;
        request = BuildNativeTextRequest(chatRequest);
        var streamId = Guid.NewGuid().ToString("n");
        var started = false;

        await foreach (var delta in ExecuteNativeStreamTextAsync(request, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(delta))
                continue;

            if (!started)
            {
                yield return streamId.ToTextStartUIMessageStreamPart();
                started = true;
            }

            yield return new TextDeltaUIMessageStreamPart
            {
                Id = streamId,
                Delta = delta
            };
        }

        if (started)
            yield return streamId.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}
