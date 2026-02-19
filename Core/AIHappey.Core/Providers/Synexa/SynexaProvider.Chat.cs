using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model.Type)
        {
            case "image":
                await foreach (var part in this.StreamImageAsync(chatRequest, cancellationToken))
                    yield return part;
                yield break;

            case "video":
                await foreach (var part in this.StreamVideoAsync(chatRequest, cancellationToken))
                    yield return part;
                yield break;

            case "transcription":
                await foreach (var part in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                    yield return part;
                yield break;

            case "language":
                {
                    var prompt = BuildPromptFromUiMessages(chatRequest.Messages);
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        yield return "No prompt provided.".ToErrorUIPart();
                        yield break;
                    }

                    var prediction = await CreatePredictionAsync(
                        chatRequest.Model,
                        new Dictionary<string, object?>
                        {
                            ["prompt"] = prompt,
                            ["temperature"] = chatRequest.Temperature,
                            ["max_tokens"] = chatRequest.MaxOutputTokens
                        },
                        cancellationToken);

                    var completed = await WaitPredictionAsync(prediction, wait: null, cancellationToken);
                    var text = ExtractOutputText(completed.Output);

                    var id = completed.Id;
                    yield return id.ToTextStartUIMessageStreamPart();

                    if (!string.IsNullOrWhiteSpace(text))
                        yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = text };

                    yield return id.ToTextEndUIMessageStreamPart();
                    yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                    yield break;
                }

            default:
                yield return $"Unsupported Synexa model type '{model.Type}'.".ToErrorUIPart();
                yield break;
        }
    }
}
