using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = chatRequest.Model;

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();

            await foreach (var part in StreamResearchUiAsync(chatRequest, model, cancellationToken))
                yield return part;

            yield break;
        }

        if (!IsAnswerModel(model))
        {
            yield return $"Exa chat streaming only supports model '{AnswerModelId}'. Requested: '{chatRequest?.Model}'."
                .ToErrorUIPart();
            yield break;
        }

        ApplyChatAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<UIMessagePart> StreamResearchUiAsync(
        ChatRequest chatRequest,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = BuildPromptFromUiMessages(chatRequest.Messages ?? []);

        if (string.IsNullOrWhiteSpace(input))
        {
            yield return "Exa research requires at least one text message.".ToErrorUIPart();
            yield break;
        }

        var outputSchema = TryExtractOutputSchema(chatRequest.ResponseFormat);
        var streamId = Guid.NewGuid().ToString("n");
        var fullText = new StringBuilder();
        object? structuredData = null;
        bool textStarted = false;
        string finishReason = "stop";

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, cancellationToken))
        {
            if (evt.IsTerminal && !string.IsNullOrWhiteSpace(evt.Error))
            {
                yield return evt.Error!.ToErrorUIPart();
                finishReason = "error";
                break;
            }

            if (!string.IsNullOrWhiteSpace(evt.ContentText))
            {
                if (!textStarted)
                {
                    yield return streamId.ToTextStartUIMessageStreamPart();
                    textStarted = true;
                }

                fullText.Append(evt.ContentText);
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = streamId,
                    Delta = evt.ContentText!
                };
            }

            if (evt.ContentObject is not null)
                structuredData = JsonSerializer.Deserialize<object>(evt.ContentObject.Value.GetRawText(), JsonSerializerOptions.Web);
        }

        if (textStarted)
            yield return streamId.ToTextEndUIMessageStreamPart();

        if (chatRequest.ResponseFormat is not null)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            var data = structuredData;

            if (data is null && fullText.Length > 0)
            {
                try
                {
                    data = JsonSerializer.Deserialize<object>(fullText.ToString(), JsonSerializerOptions.Web);
                }
                catch
                {
                    // ignore structured parse errors
                }
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

        yield return finishReason.ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest.Temperature);
    }
}
