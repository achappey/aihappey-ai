using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private static (string? Text, List<AIToolCallContentPart> Approvals) LatestInput(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Dust agents execute their configured tools; gateway client tool definitions are not supported.");

        var items = request.Input?.Items ?? [];
        var lastUserIndex = items.FindLastIndex(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var lastAssistantIndex = items.FindLastIndex(item => string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        var last = items.LastOrDefault();
        var approvals = (last?.Content?.OfType<AIToolCallContentPart>() ?? [])
            .Where(tool => tool.State == "approval-responded" && tool.Approval?.Approved is not null)
            .ToList();
        if (approvals.Count > 0)
            return (null, approvals);

        // Only the current turn may be submitted: historical user messages are already stored by Dust.
        if (lastUserIndex >= 0 && lastUserIndex > lastAssistantIndex)
        {
            var user = items[lastUserIndex];
            if (user.Content?.Any(part => part is not AITextContentPart) == true)
                throw new NotSupportedException("Dust currently accepts text messages only.");
            var text = string.Join("\n", user.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return (text, []);
        }
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            return (request.Input.Text, []);
        throw new ArgumentException("Dust requires a new user message or an approval response in the latest input item.");
    }
}
