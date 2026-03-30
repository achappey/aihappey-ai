using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
        => StreamUiCoreAsync(chatRequest, cancellationToken);

    private async IAsyncEnumerable<UIMessagePart> StreamUiCoreAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "You.com requires at least one text message.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var metadata = ReadChatMetadata(chatRequest);

        if (IsResearchModel(chatRequest.Model))
        {
            if (chatRequest.Tools?.Any() == true)
            {
                yield return "You.com research models do not support tool definitions. Use agent models for grounded agent behavior."
                    .ToErrorUIPart();
                yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
                yield break;
            }

            var result = await ExecuteResearchAsync(chatRequest.Model, prompt, chatRequest.ResponseFormat, metadata.ResearchEffort, cancellationToken);
            var streamId = result.Id;

            yield return streamId.ToTextStartUIMessageStreamPart();
            foreach (var chunk in ChunkText(result.Text))
            {
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = streamId,
                    Delta = chunk
                };
            }

            yield return streamId.ToTextEndUIMessageStreamPart();

            foreach (var source in result.Sources)
                yield return ToSourcePart(source);

            var structured = TryParseStructuredOutput(result.Text, chatRequest.ResponseFormat);
            if (structured is not null)
            {
                var schema = chatRequest.ResponseFormat.GetJSONSchema();
                yield return new DataUIPart
                {
                    Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                    Data = structured
                };
            }

            yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        if (!IsAgentModel(chatRequest.Model))
        {
            yield return $"Unsupported You.com UI model '{chatRequest.Model}'.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var responseId = Guid.NewGuid().ToString("n");
        var fullText = new StringBuilder();
        var textStarted = false;
        var emittedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finishReason = "error";
        long? runtimeMs = null;

        await foreach (var evt in StreamAgentEventsAsync(chatRequest.Model, prompt, chatRequest.ResponseFormat, chatRequest.Tools?.Any() == true, metadata, cancellationToken))
        {
            foreach (var source in evt.Sources ?? [])
            {
                if (emittedSources.Add(source.Url))
                    yield return ToSourcePart(source);
            }

            if (!string.IsNullOrWhiteSpace(evt.Delta))
            {
                if (!textStarted)
                {
                    yield return responseId.ToTextStartUIMessageStreamPart();
                    textStarted = true;
                }

                fullText.Append(evt.Delta);
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = responseId,
                    Delta = evt.Delta!
                };
            }

            if (evt.Type == "response.done")
            {
                finishReason = evt.Finished == false ? "error" : "stop";
                runtimeMs = evt.RuntimeMs;
            }
        }

        if (textStarted)
            yield return responseId.ToTextEndUIMessageStreamPart();

        var parsed = TryParseStructuredOutput(fullText.ToString(), chatRequest.ResponseFormat);
        if (parsed is not null)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            yield return new DataUIPart
            {
                Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                Data = parsed
            };
        }

        Dictionary<string, object>? extraMetadata = null;
        if (runtimeMs is not null)
        {
            extraMetadata = new Dictionary<string, object>
            {
                ["runtimeMs"] = runtimeMs.Value
            };
        }

        yield return finishReason.ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature, extraMetadata: extraMetadata);
    }
}
