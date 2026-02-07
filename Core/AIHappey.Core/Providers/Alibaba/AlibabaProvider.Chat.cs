using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        if (model.Type == "image")
        {
            await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "video")
        {
            await foreach (var p in this.StreamVideoAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "transcription")
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        // DashScope OpenAI-compatible endpoint:
        // POST https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "compatible-mode/v1/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

