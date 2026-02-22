using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => ExtractChatMessageTexts(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var translated = await TranslateTextsFromModelAsync(modelId, texts, cancellationToken);
        var joined = string.Join("\n", translated);

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = joined },
                    finish_reason = "stop"
                }
            ],
            Usage = null
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => ExtractChatMessageTexts(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ]
        };

        var translated = await TranslateTextsFromModelAsync(modelId, texts, cancellationToken);
        for (var i = 0; i < translated.Count; i++)
        {
            var text = translated[i];
            var piece = (i == translated.Count - 1) ? text : (text + "\n");

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = modelId,
                Choices =
                [
                    new { index = 0, delta = new { content = piece }, finish_reason = (string?)null }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ]
        };
    }

}

