using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private async Task<ChatCompletion> CompleteNimbleWayChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("NimbleWay requires non-empty input derived from chat messages.");

        var result = await ExecuteNimbleWayAsync(options.Model, query, passthrough: null, cancellationToken);
        var parts = BuildOrderedTextParts(result)
            .Select(text => new { type = "text", text })
            .ToArray();

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = UnixNow(),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = parts,
                        metadata = BuildResultMetadata(result)
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            }
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteNimbleWayChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteNimbleWayChatAsync(options, cancellationToken);
        var id = completion.Id;
        var created = completion.Created;
        var model = completion.Model;

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ],
            Usage = null
        };

        foreach (var segment in BuildChatCompletionTextSegments(completion))
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new { index = 0, delta = new { content = segment }, finish_reason = (string?)null }
                ],
                Usage = null
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ],
            Usage = completion.Usage
        };
    }

    private static IEnumerable<string> BuildChatCompletionTextSegments(ChatCompletion completion)
    {
        var first = completion.Choices.FirstOrDefault();
        if (first is not System.Text.Json.JsonElement choiceEl || choiceEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return [];

        if (!choiceEl.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return [];

        if (!msgEl.TryGetProperty("content", out var contentEl))
            return [];

        if (contentEl.ValueKind == System.Text.Json.JsonValueKind.String)
            return [contentEl.GetString() ?? string.Empty];

        if (contentEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var segments = new List<string>();
        foreach (var part in contentEl.EnumerateArray())
        {
            if (part.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                segments.Add(part.GetString() ?? string.Empty);
                continue;
            }

            if (part.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                segments.Add(textEl.GetString() ?? string.Empty);
        }

        return segments;
    }
}

