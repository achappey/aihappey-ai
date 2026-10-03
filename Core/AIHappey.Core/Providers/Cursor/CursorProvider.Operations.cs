using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cursor;

public sealed partial class CursorProvider
{
    /// <summary>Fixed endpoint dispatcher; options use operation, body, query and explicit path IDs.</summary>
    public Task<JsonElement> ExecuteOperationAsync(JsonElement options, CancellationToken cancellationToken = default)
        => Dispatch(Client(), options, cancellationToken);

    private static string Required(JsonElement options, string key) => Str(options, key) is { Length: > 0 } value
        ? value : throw new ArgumentException($"Cursor operation requires {key}.");
    private static object RequiredBody(JsonElement options) => Prop(options, "body") is { ValueKind: JsonValueKind.Object } body
        ? body : throw new ArgumentException("Cursor operation requires an object body.");
    private static object RequiredQuery(JsonElement options) => Prop(options, "query") is { ValueKind: JsonValueKind.Object } query
        ? query : throw new ArgumentException("Cursor operation requires an object query.");

    private static Task<JsonElement> Dispatch(CursorApiClient api, JsonElement options, CancellationToken ct)
    {
        var query = Prop(options, "query");
        string Agent() => Required(options, "agentId");
        string Run() => Required(options, "runId");
        return Required(options, "operation") switch
        {
            "createAgent" => api.CreateAgentAsync(RequiredBody(options), ct),
            "listAgents" => api.ListAgentsAsync(query, ct),
            "getAgent" => api.GetAgentAsync(Agent(), ct),
            "createRun" => api.CreateRunAsync(Agent(), RequiredBody(options), ct),
            "listRuns" => api.ListRunsAsync(Agent(), query, ct),
            "getRun" => api.GetRunAsync(Agent(), Run(), ct),
            "cancelRun" => api.CancelRunAsync(Agent(), Run(), ct),
            "getUsage" => api.GetUsageAsync(Agent(), query, ct),
            "listArtifacts" => api.ListArtifactsAsync(Agent(), query, ct),
            "downloadArtifact" => api.DownloadArtifactAsync(Agent(), RequiredQuery(options), ct),
            "archiveAgent" => api.ArchiveAgentAsync(Agent(), ct),
            "unarchiveAgent" => api.UnarchiveAgentAsync(Agent(), ct),
            "deleteAgent" => api.DeleteAgentAsync(Agent(), ct),
            "createSubToken" => api.CreateSubTokenAsync(RequiredBody(options), ct),
            "getMe" => api.GetMeAsync(ct),
            "listModels" => api.ListModelsAsync(ct),
            "listRepositories" => api.ListRepositoriesAsync(ct),
            "listPrivateWorkers" => api.ListPrivateWorkersAsync(query, ct),
            "getPrivateWorkersSummary" => api.GetPrivateWorkersSummaryAsync(query, ct),
            "getPrivateWorker" => api.GetPrivateWorkerAsync(Required(options, "workerId"), ct),
            "listWorkerPools" => api.ListWorkerPoolsAsync(query, ct),
            "createWorkerPool" => api.CreateWorkerPoolAsync(RequiredBody(options), ct),
            "deleteWorkerPool" => api.DeleteWorkerPoolAsync(RequiredQuery(options), ct),
            "listPendingRequests" => api.ListPendingRequestsAsync(query, ct),
            "claimPendingRequest" => api.ClaimPendingRequestAsync(RequiredBody(options), ct),
            "createWorkerTokens" => api.CreateWorkerTokensAsync(RequiredBody(options), ct),
            "releaseWorkerClaim" => api.ReleaseWorkerClaimAsync(Required(options, "claimId"), ct),
            "streamRun" or "streamPendingRequests" => CollectOperationStream(api, options, ct),
            _ => throw new NotSupportedException("Unknown Cursor operation. Arbitrary HTTP methods and URLs are not supported.")
        };
    }

    public IAsyncEnumerable<CursorSseEvent> StreamOperationAsync(JsonElement options, CancellationToken cancellationToken = default)
        => OperationStream(Client(), options, cancellationToken);

    private static IAsyncEnumerable<CursorSseEvent> OperationStream(CursorApiClient api, JsonElement options, CancellationToken ct)
        => Required(options, "operation") switch
        {
            "streamRun" => api.StreamRunAsync(Required(options, "agentId"), Required(options, "runId"), Str(options, "lastEventId"), Prop(options, "query"), ct),
            "streamPendingRequests" => api.StreamPendingRequestsAsync(RequiredQuery(options), Str(options, "lastEventId"), ct),
            _ => throw new ArgumentException("Cursor streaming operation must be streamRun or streamPendingRequests.")
        };

    private static async Task<JsonElement> CollectOperationStream(CursorApiClient api, JsonElement options, CancellationToken ct)
    {
        var events = new List<CursorSseEvent>();
        var limit = Math.Clamp(Int(options, "maxEvents") ?? 1000, 1, 10000);
        await foreach (var frame in OperationStream(api, options, ct))
        {
            events.Add(frame);
            if (frame.Event == "done" || events.Count >= limit) break;
        }
        return Element(new { events });
    }
}
