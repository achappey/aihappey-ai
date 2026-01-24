using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var result = await TranslateAsync(texts, options.Model.Split("/").Last()!, cancellationToken);
        var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;

        // First chunk: role
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ]
        };

        var result = await TranslateAsync(texts, options.Model.Split("/").Last()!, cancellationToken);

        for (var i = 0; i < result.Translations.Count; i++)
        {
            var t = result.Translations[i].TranslatedText;
            var piece = (i == result.Translations.Count - 1) ? t : (t + "\n");

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = options.Model,
                Choices =
                [
                    new { index = 0, delta = new { content = piece }, finish_reason = (string?)null }
                ]
            };

            seq++;
        }

        // Final chunk
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ]
        };
    }
}

