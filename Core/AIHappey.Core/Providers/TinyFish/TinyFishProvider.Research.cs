using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.TinyFish;

public partial class TinyFishProvider
{
    private static readonly HashSet<string> ResearchOptions = new(StringComparer.Ordinal)
    {
        "mode", "output_language", "weak_sources_enabled", "domain_type", "after_date", "before_date",
        "recency_minutes", "domain_filter", "prior_run_id", "session_id"
    };

    private static Dictionary<string, object?> BuildResearchPayload(AIRequest request, TinyFishTarget target, bool streaming)
    {
        var query = BuildPrompt(request);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
            throw new InvalidOperationException("TinyFish research requires a query of one to 2000 characters derived from unified input or instructions.");

        var payload = ConvertExtraProperties(target.Metadata.AdditionalProperties)
            .Where(option => ResearchOptions.Contains(option.Key))
            .ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);
        if (target.ResearchMode is not null)
            payload["mode"] = target.ResearchMode;
        if (payload.TryGetValue("mode", out var mode) && mode is not null)
        {
            var modeValue = JsonSerializer.SerializeToElement(mode);
            if (modeValue.ValueKind != JsonValueKind.String || modeValue.GetString() is not ("auto" or "standard" or "deep" or "max"))
                throw new InvalidOperationException("TinyFish research mode must be auto, standard, deep, or max.");
        }
        bool HasOption(string name) => payload.TryGetValue(name, out var value) && value is not null;
        if (HasOption("prior_run_id") && HasOption("session_id"))
            throw new InvalidOperationException("TinyFish research prior_run_id and session_id are mutually exclusive.");
        if (HasOption("recency_minutes") && (HasOption("after_date") || HasOption("before_date")))
            throw new InvalidOperationException("TinyFish research recency_minutes cannot be combined with date filters.");
        payload["query"] = query;
        payload["stream"] = streaming;
        return payload;
    }

    private async Task<AIResponse> ExecuteResearchAsync(AIRequest request, TinyFishTarget target, CancellationToken cancellationToken)
    {
        var payload = BuildResearchPayload(request, target, false);
        var state = new TinyFishResearchState();
        await foreach (var _ in ReadResearchEventsAsync(payload, state, cancellationToken)) { }

        var output = new List<AIOutputItem>();
        if (state.Report is not null)
            output.Add(CreateMessageItem(state.Report));
        foreach (var url in state.Citations)
            output.Add(CreateSourceOutputItem(url, url, "research_citation"));
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToUnifiedModel(target.Model),
            Status = state.Error is null ? "completed" : "failed",
            Output = new AIOutput { Items = output },
            Metadata = CreateResearchMetadata(payload, state),
            Usage = state.Statistics.ValueKind == JsonValueKind.Object
                ? state.Statistics.EnumerateObject().ToDictionary(property => property.Name, property => ToPlainObject(property.Value))
                : new Dictionary<string, object?>()
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamResearchAsync(
        AIRequest request, TinyFishTarget target, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildResearchPayload(request, target, true);
        var state = new TinyFishResearchState();
        var eventId = request.Id ?? $"tinyfish_research_{Guid.NewGuid():N}";
        await foreach (var researchEvent in ReadResearchEventsAsync(payload, state, cancellationToken))
        {
            yield return CreateStreamEvent("data-tinyfish.research", eventId, new AIDataEventData
            {
                Id = researchEvent.Frame.Id ?? state.RunId ?? eventId,
                Data = researchEvent.Raw,
                Transient = false
            }, DateTimeOffset.UtcNow, new Dictionary<string, object?>
            {
                ["tinyfish.research.run_id"] = state.RunId,
                ["tinyfish.stream.event_name"] = researchEvent.Name,
                ["tinyfish.stream.event_id"] = researchEvent.Frame.Id
            });
        }

        var metadata = CreateResearchMetadata(payload, state);
        var timestamp = DateTimeOffset.UtcNow;
        // Until the provider documents its delta schema, emit the authoritative final report once.
        // Synthesis/progress events are still delivered above as complete, inspectable JSON.
        if (state.Error is null && state.Report is not null)
        {
            yield return CreateStreamEvent("text-start", eventId, new AITextStartEventData(), timestamp, metadata);
            yield return CreateStreamEvent("text-delta", eventId, new AITextDeltaEventData { Delta = state.Report }, timestamp, metadata);
            yield return CreateStreamEvent("text-end", eventId, new AITextEndEventData(), timestamp, metadata);
            foreach (var url in state.Citations)
                yield return CreateSourceStreamEvent(CreateResultId(eventId, url),
                    new TinyFishFetchResult { Url = url, Title = url }, timestamp, metadata, "research_citation");
        }
        if (state.Error is not null)
            yield return CreateStreamEvent("error", eventId, new AIErrorEventData { ErrorText = state.Error }, timestamp, metadata);
        yield return CreateFinishStreamEvent(eventId, request, state.Error is null ? "stop" : "error", timestamp, metadata);
    }

    private async IAsyncEnumerable<TinyFishResearchEvent> ReadResearchEventsAsync(
        Dictionary<string, object?> payload, TinyFishResearchState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, "v1/automation/run-research", payload);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"TinyFish research failed ({(int)response.StatusCode}): {body}");
        }
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TinyFish research returned a non-SSE response.");

        await foreach (var frame in ReadSseFramesAsync(response, cancellationToken))
        {
            var raw = ParseSseJson(frame.Data);
            var name = GetString(raw, "event") ?? frame.Event ?? GetString(raw, "type") ?? "unknown";
            state.RunId = GetString(raw, "research_run_id") ?? state.RunId;
            if (name == "final_result")
            {
                state.FinalResult = raw;
                state.Report = GetString(raw, "result");
                state.Citations = DistinctUrls(GetStringArray(raw, "citations"));
                state.Statistics = CloneProperty(raw, "stats");
            }
            if (name is "error" or "failed" || GetString(raw, "status") is "FAILED" or "CANCELLED" or "TIMED_OUT")
                state.Error = GetErrorText(raw) ?? GetString(raw, "message") ?? $"TinyFish research ended with event '{name}'.";
            state.LastEvent = raw;
            yield return new TinyFishResearchEvent(frame, name, raw);
            if (name == "done")
            {
                if (state.Report is null && state.Error is null)
                    throw new InvalidOperationException("TinyFish research completed without a final report.");
                yield break;
            }
        }
        throw new InvalidOperationException("TinyFish research stream ended before a done event was received.");
    }

    private static Dictionary<string, object?> CreateResearchMetadata(Dictionary<string, object?> payload, TinyFishResearchState state)
        => new()
        {
            ["tinyfish.research.run_id"] = state.RunId,
            ["tinyfish.research.mode"] = payload.GetValueOrDefault("mode") ?? "deep",
            ["tinyfish.research.effective_mode"] = payload.GetValueOrDefault("mode")?.ToString() == "auto"
                ? "standard" : payload.GetValueOrDefault("mode") ?? "deep",
            ["tinyfish.research.final_result"] = state.FinalResult,
            ["tinyfish.research.last_event"] = state.LastEvent,
            ["tinyfish.research.error"] = state.Error,
            ["tinyfish.research.stats"] = state.Statistics
        };

    private static JsonElement ParseSseJson(string data)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("TinyFish SSE events must contain a JSON object.");
        return document.RootElement.Clone();
    }

    private static async IAsyncEnumerable<TinyFishSseFrame> ReadSseFramesAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? eventName = null;
        string? eventId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                    yield return new TinyFishSseFrame(data.ToString(), eventName, eventId);
                data.Clear();
                eventName = null;
                eventId = null;
                if (line is null)
                    yield break;
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            switch (field)
            {
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
                case "event": eventName = value; break;
                case "id": eventId = value; break;
            }
        }
    }

    private sealed record TinyFishSseFrame(string Data, string? Event, string? Id);
    private sealed record TinyFishResearchEvent(TinyFishSseFrame Frame, string Name, JsonElement Raw);
    private sealed class TinyFishResearchState
    {
        public string? RunId { get; set; }
        public string? Report { get; set; }
        public string? Error { get; set; }
        public List<string> Citations { get; set; } = [];
        public JsonElement FinalResult { get; set; }
        public JsonElement LastEvent { get; set; }
        public JsonElement Statistics { get; set; }
    }
}
