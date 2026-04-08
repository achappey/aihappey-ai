using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.StealthGPT;

public partial class StealthGPTProvider
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

        var native = await ExecuteNativeTextAsync(
            modelId,
            prompt,
            chatRequest.ProviderMetadata?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            cancellationToken);

        var streamId = Guid.NewGuid().ToString("n");
        yield return streamId.ToTextStartUIMessageStreamPart();

        foreach (var chunk in ChunkText(native.OutputText))
        {
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = streamId,
                Delta = chunk
            };
        }

        yield return streamId.ToTextEndUIMessageStreamPart();
        yield return new DataUIPart
        {
            Type = "data-stealthgpt-metadata",
            Data = native.ProviderMetadata
        };
        yield return "stop".ToFinishUIPart(modelId, 0, 0, 0, chatRequest.Temperature);
    }
}
