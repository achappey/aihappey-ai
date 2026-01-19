using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

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

        var parts = lastUser.Parts ?? [];

        for (var i = 0; i < parts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var part = parts[i];

            switch (part)
            {
                case TextUIPart text:
                {
                    // each text part gets its own stream id
                    var id = $"echo:{chatRequest.Id}:{lastUser.Id}:{i}";

                    yield return id.ToTextStartUIMessageStreamPart();

                    foreach (var ch in text.Text ?? string.Empty)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = ch.ToString() };
                    }

                    yield return id.ToTextEndUIMessageStreamPart();
                    break;
                }

                case FileUIPart:
                    // Echo file parts as-is.
                    yield return part;
                    break;

                default:
                    // Best-effort: pass through any other UIMessagePart types.
                    yield return part;
                    break;
            }
        }

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest.Temperature);

        await Task.CompletedTask;
    }
}

