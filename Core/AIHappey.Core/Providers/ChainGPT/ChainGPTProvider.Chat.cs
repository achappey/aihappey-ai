using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ChainGPT;

public partial class ChainGPTProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);
        ApplyAuthHeader();

        var modelId = chatRequest.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        var metadata = chatRequest.GetProviderMetadata<ChainGPTProviderMetadata>(GetIdentifier());

        var streamId = Guid.NewGuid().ToString("n");
        var started = false;

        await foreach (var chunk in CompleteQuestionStreamingAsync(modelId, prompt, metadata, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            if (!started)
            {
                yield return streamId.ToTextStartUIMessageStreamPart();
                started = true;
            }

            yield return new TextDeltaUIMessageStreamPart
            {
                Id = streamId,
                Delta = chunk
            };
        }

        if (started)
            yield return streamId.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(modelId, 0, 0, 0, chatRequest.Temperature);
    }
}
