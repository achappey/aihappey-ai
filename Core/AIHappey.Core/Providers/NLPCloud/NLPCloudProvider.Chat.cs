using System.Runtime.CompilerServices;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    private sealed record NLPCloudHistoryItem(string Input, string Response);

    private sealed record NLPCloudChatbotRequest(
        string Input,
        string? Context,
        IReadOnlyList<NLPCloudHistoryItem> History);

    private static NLPCloudChatbotRequest BuildChatbotRequest(
        IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUserIndex = -1;
        for (var index = 0; index < messages.Count; index++)
            if (string.Equals(messages[index].Role, "user", StringComparison.OrdinalIgnoreCase))
                lastUserIndex = index;

        if (lastUserIndex < 0 || string.IsNullOrWhiteSpace(messages[lastUserIndex].Content))
            throw new ArgumentException("No user input provided.", nameof(messages));

        var contextParts = messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(m.Role, "developer", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var history = new List<NLPCloudHistoryItem>();
        string? pendingUser = null;
        foreach (var message in messages.Take(lastUserIndex))
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                pendingUser = message.Content;
            else if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(pendingUser) && !string.IsNullOrWhiteSpace(message.Content))
                    history.Add(new NLPCloudHistoryItem(pendingUser, message.Content));
                pendingUser = null;
            }
        }

        return new NLPCloudChatbotRequest(
            messages[lastUserIndex].Content!,
            contextParts.Count == 0 ? null : string.Join("\n", contextParts),
            history);
    }
}
