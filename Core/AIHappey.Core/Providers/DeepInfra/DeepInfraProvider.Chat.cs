using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
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

        if (model.Type == "speech")
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
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

        // POST https://api.deepinfra.com/v1/openai/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "v1/openai/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

