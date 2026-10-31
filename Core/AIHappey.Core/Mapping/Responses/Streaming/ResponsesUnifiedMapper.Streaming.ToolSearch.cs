using AIHappey.Responses.Streaming;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly AsyncLocal<ToolSearchStreamState?> CurrentToolSearchStreamState = new();

    private static ToolSearchStreamState ToolSearchState
        => CurrentToolSearchStreamState.Value ??= new ToolSearchStreamState();

    private static void ClearToolSearchStreamState()
        => CurrentToolSearchStreamState.Value = null;

    private static string RegisterToolSearchCall(ResponseStreamItem item)
    {
        var unifiedId = item.CallId ?? item.Id ?? Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(item.CallId))
            ToolSearchState.CallIds[item.CallId] = unifiedId;

        if (!string.IsNullOrWhiteSpace(item.Id))
            ToolSearchState.ItemIds[item.Id] = unifiedId;

        if (IsServerToolSearch(item) && string.IsNullOrWhiteSpace(item.CallId))
            ToolSearchState.PendingHostedCallIds.Enqueue(unifiedId);

        return unifiedId;
    }

    private static string RegisterToolSearchOutput(ResponseStreamItem item)
    {
        var unifiedId = ResolveRegisteredToolSearchId(item);

        if (IsServerToolSearch(item)
            && string.IsNullOrWhiteSpace(item.CallId)
            && ToolSearchState.PendingHostedCallIds.TryDequeue(out var hostedCallId))
        {
            unifiedId = hostedCallId;
        }

        if (!string.IsNullOrWhiteSpace(item.Id))
            ToolSearchState.ItemIds[item.Id] = unifiedId;

        return unifiedId;
    }

    private static string ResolveRegisteredToolSearchId(ResponseStreamItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CallId)
            && ToolSearchState.CallIds.TryGetValue(item.CallId, out var byCallId))
        {
            return byCallId;
        }

        if (!string.IsNullOrWhiteSpace(item.Id)
            && ToolSearchState.ItemIds.TryGetValue(item.Id, out var byItemId))
        {
            return byItemId;
        }

        return item.CallId ?? item.Id ?? Guid.NewGuid().ToString("N");
    }

    private sealed class ToolSearchStreamState
    {
        public Queue<string> PendingHostedCallIds { get; } = new();

        public Dictionary<string, string> CallIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ItemIds { get; } = new(StringComparer.Ordinal);
    }
}
