using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{


    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
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

        var translated = await ProcessTextsAsync(texts, modelId, cancellationToken);
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        var translated = await ProcessTextsAsync(texts, modelId, cancellationToken);

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

    private static IEnumerable<string> ExtractChatMessageTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text!;

            yield break;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (!part.TryGetProperty("type", out var typeProp)
                    || !string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!part.TryGetProperty("text", out var textProp)
                    || textProp.ValueKind != JsonValueKind.String)
                    continue;

                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text!;
            }
        }
    }
}
