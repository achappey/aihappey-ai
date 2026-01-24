using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Echo;

public sealed partial class EchoProvider
{
    private async IAsyncEnumerable<UIMessagePart> StreamEchoAsync(
     ChatRequest chatRequest,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == Role.user);

        if (lastUser == null)
        {
            yield return "Echo provider: no user message found.".ToErrorUIPart();
            yield return "stop".ToFinishUIPart(
                model: chatRequest.Model,
                outputTokens: 0,
                inputTokens: 0,
                totalTokens: 0,
                temperature: chatRequest.Temperature);
            yield break;
        }

        // ✅ ALWAYS use a fresh assistant stream id (never derived from user msg id)

        //  yield return new StepStartUIPart();

        var parts = lastUser.Parts ?? [];

        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (part is TextUIPart text)
            {
                var assistantId = $"echo:{chatRequest.Id}:{Guid.NewGuid():N}";

                yield return assistantId.ToTextStartUIMessageStreamPart();

                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = assistantId,
                    Delta = text.Text
                };

                yield return assistantId.ToTextEndUIMessageStreamPart();
                /* foreach (var ch in text.Text ?? string.Empty)
                 {
                     cancellationToken.ThrowIfCancellationRequested();

                     yield return new TextDeltaUIMessageStreamPart
                     {
                         Id = assistantId,
                         Delta = ch.ToString()
                     };
                 }*/
            }

            if (part is FileUIPart filePart)
            {
                yield return filePart;
            }

            // optional: ignore files or echo them if you want
            // else if (part is FileUIPart file) yield return file;
        }



        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest.Temperature);

        yield break;
    }


}

