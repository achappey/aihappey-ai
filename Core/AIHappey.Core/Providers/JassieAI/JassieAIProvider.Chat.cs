using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.JassieAI;

public partial class JassieAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var options = BuildChatOptionsFromChatRequest(chatRequest, stream: true);
        var payload = BuildNativeRequest(options, stream: true);
        var endpoint = ResolveEndpoint(payload.Model);

        string textId = Guid.NewGuid().ToString("n");
        string reasoningId = Guid.NewGuid().ToString("n");
        bool textStarted = false;
        bool reasoningStarted = false;
        var finalText = new StringBuilder();

        await foreach (var chunk in StreamNativeAsync(payload, endpoint, cancellationToken))
        {
            var reasoning = ExtractReasoningText(chunk);
            if (!string.IsNullOrEmpty(reasoning))
            {
                if (!reasoningStarted)
                {
                    yield return new ReasoningStartUIPart { Id = reasoningId };
                    reasoningStarted = true;
                }

                yield return new ReasoningDeltaUIPart
                {
                    Id = reasoningId,
                    Delta = reasoning
                };

                continue;
            }

            var text = ExtractAssistantText(chunk);
            if (string.IsNullOrEmpty(text))
                continue;

            if (!textStarted)
            {
                yield return textId.ToTextStartUIMessageStreamPart();
                textStarted = true;
            }

            finalText.Append(text);
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = textId,
                Delta = text
            };
        }

        if (reasoningStarted)
            yield return new ReasoningEndUIPart { Id = reasoningId };

        if (textStarted)
            yield return textId.ToTextEndUIMessageStreamPart();

        if (chatRequest.ResponseFormat != null && finalText.Length > 0)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            object? data;
            try
            {
                data = JsonSerializer.Deserialize<object>(finalText.ToString(), Json);
            }
            catch
            {
                data = null;
            }

            if (data is not null)
            {
                yield return new DataUIPart
                {
                    Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                    Data = data
                };
            }
        }
    }
}
