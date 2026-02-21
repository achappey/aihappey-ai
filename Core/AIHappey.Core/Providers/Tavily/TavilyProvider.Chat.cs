using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using AIHappey.Common.Extensions;
using System.Text;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var part in StreamResearchUiAsync(chatRequest, cancellationToken))
            yield return part;
    }

    private async IAsyncEnumerable<UIMessagePart> StreamResearchUiAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = chatRequest.Model;
        var input = BuildPromptFromUiMessages(chatRequest.Messages);

        if (string.IsNullOrWhiteSpace(input))
        {
            yield return "Tavily requires at least one text message.".ToErrorUIPart();
            yield break;
        }

        var outputSchema = TryExtractOutputSchema(chatRequest.ResponseFormat);

        var streamId = Guid.NewGuid().ToString("n");
        var fullText = new StringBuilder();
        object? structuredData = null;
        bool textStarted = false;
        string finishReason = "stop";

        int inputTokens = 0;
        int outputTokens = 0;
        int totalTokens = 0;

        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.FinishReason))
                finishReason = evt.FinishReason!;

            if (evt.Usage is not null)
                ExtractUsage(evt.Usage.Value, ref inputTokens, ref outputTokens, ref totalTokens);

            foreach (var source in evt.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url) || !seenSources.Add(source.Url))
                    continue;

                yield return ToSourcePart(source);
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
            {
                structuredData = JsonSerializer.Deserialize<object>(evt.ContentObject.Value.GetRawText(), JsonSerializerOptions.Web);
            }
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
                    // Ignore malformed JSON in model text output for structured-mode UX.
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
            outputTokens: outputTokens,
            inputTokens: inputTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }



    private static SourceUIPart ToSourcePart(TavilySource source)
        => new()
        {
            SourceId = source.Url,
            Url = source.Url,
            Title = source.Title,
            ProviderMetadata = string.IsNullOrWhiteSpace(source.Favicon)
                ? null
                : new Dictionary<string, object>
                {
                    ["favicon"] = source.Favicon!
                }
        };


    private static void ExtractUsage(JsonElement usage, ref int inputTokens, ref int outputTokens, ref int totalTokens)
    {
        if (usage.TryGetProperty("prompt_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number)
            inputTokens = inEl.GetInt32();

        if (usage.TryGetProperty("completion_tokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number)
            outputTokens = outEl.GetInt32();

        if (usage.TryGetProperty("total_tokens", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number)
            totalTokens = totalEl.GetInt32();
        else
            totalTokens = inputTokens + outputTokens;
    }

}
