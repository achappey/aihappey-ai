using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Inferencesh;

public partial class InferenceshProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(chatRequest);

        var app = ResolveInferenceAppId(chatRequest.Model);
        var prompt = BuildPromptFromUiMessages(chatRequest.Messages);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var runTask = await RunTaskAsync(
            app,
            prompt,
            chatRequest.Temperature,
            chatRequest.MaxOutputTokens,
            chatRequest.TopP,
            cancellationToken);

        var streamId = runTask.Id;
        var textStarted = false;
        var fullText = string.Empty;

        await foreach (var evt in StreamTaskEventsAsync(runTask.Id, cancellationToken: cancellationToken))
        {
            if (evt.Task is null)
                continue;

            var newText = ExtractTaskText(evt.Task);
            if (!string.IsNullOrWhiteSpace(newText))
            {
                var delta = CalculateDelta(fullText, newText);
                if (!string.IsNullOrWhiteSpace(delta))
                {
                    if (!textStarted)
                    {
                        yield return streamId.ToTextStartUIMessageStreamPart();
                        textStarted = true;
                    }

                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = streamId,
                        Delta = delta
                    };
                }

                fullText = newText;
            }

            if (!evt.IsTerminal)
                continue;

            if (textStarted)
                yield return streamId.ToTextEndUIMessageStreamPart();

            if (!evt.IsSuccess)
            {
                yield return (evt.Task.Error ?? "Inference.sh task failed.").ToErrorUIPart();
                yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                yield break;
            }

            yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        if (textStarted)
            yield return streamId.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}
