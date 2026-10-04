using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = ResolveModel(request.Model);
        ApplyAuthHeader();
        return model is "crawl" or "extract"
            ? await ExecuteWebAsync(request, model, cancellationToken)
            : await ExecuteResearchAsync(request, model, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = ResolveModel(request.Model);
        ApplyAuthHeader();
        if (model is "crawl" or "extract")
        {
            await foreach (var evt in StreamWebAsync(request, model, cancellationToken))
                yield return evt;
            yield break;
        }

        var payload = CreateResearchPayload(request, model, stream: true);
        var id = request.Id ?? $"tavily_{Guid.NewGuid():N}";
        var provider = GetIdentifier();
        var textStarted = false;
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, object?> { ["tavily.model"] = model };
        var sourceUrls = new HashSet<string>(StringComparer.Ordinal);
        var toolInputs = new HashSet<string>(StringComparer.Ordinal);
        var toolOutputs = new HashSet<string>(StringComparer.Ordinal);
        object? usage = null;
        var finishReason = "stop";

        await foreach (var root in StreamResearchEventsAsync(payload, cancellationToken))
        {
            timestamp = GetProperty(root, "created") is { ValueKind: JsonValueKind.Number } created
                ? DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()) : DateTimeOffset.UtcNow;
            metadata = new Dictionary<string, object?>(metadata)
            {
                ["tavily.stream.raw"] = root.Clone(),
                ["tavily.stream.id"] = GetString(root, "id"),
                ["tavily.stream.model"] = GetString(root, "model") ?? model,
                ["tavily"] = root.Clone()
            };
            if (GetProperty(root, "usage") is { ValueKind: JsonValueKind.Object } currentUsage)
                metadata["tavily.usage"] = usage = currentUsage.Clone();
            if (GetProperty(root, "choices") is not { ValueKind: JsonValueKind.Array } choices)
                continue;
            foreach (var choice in choices.EnumerateArray())
            {
                finishReason = GetString(choice, "finish_reason") ?? finishReason;
                if (GetProperty(choice, "delta") is not { ValueKind: JsonValueKind.Object } delta)
                    continue;
                metadata["tavily.stream.delta"] = delta.Clone();
                if (GetProperty(delta, "tool_calls") is { ValueKind: JsonValueKind.Object } tools)
                {
                    foreach (var evt in MapToolEvents(tools, provider, timestamp, metadata, toolInputs, toolOutputs))
                        yield return evt;
                }
                var sources = GetProperty(delta, "sources") is JsonElement sourceArray ? ParseSources(sourceArray) : [];
                if (GetProperty(delta, "tool_calls") is JsonElement calls
                    && GetProperty(calls, "tool_response") is { ValueKind: JsonValueKind.Array } responses)
                {
                    foreach (var tool in responses.EnumerateArray())
                        if (GetProperty(tool, "sources") is JsonElement nested)
                            sources.AddRange(ParseSources(nested));
                }
                foreach (var source in sources)
                    if (sourceUrls.Add(UrlKey(source.Url)))
                        yield return CreateSourceStreamEvent(provider, source, timestamp, metadata, id);

                if (GetProperty(delta, "content") is not JsonElement content
                    || content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content.GetRawText();
                if (content.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    metadata["tavily.structured_output"] = content.Clone();
                    yield return CreateStreamEvent(provider, "data-tavily.structured-output", id, timestamp,
                        new AIDataEventData { Id = id, Data = content.Clone() }, metadata);
                }
                if (text.Length == 0)
                    continue;
                var providerMetadata = TextProviderMetadata(metadata);
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateStreamEvent(provider, "text-start", id, timestamp,
                        new AITextStartEventData { ProviderMetadata = providerMetadata }, metadata);
                }
                yield return CreateStreamEvent(provider, "text-delta", id, timestamp,
                    new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, metadata);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (textStarted)
            yield return CreateStreamEvent(provider, "text-end", id, timestamp,
                new AITextEndEventData { ProviderMetadata = TextProviderMetadata(metadata) }, metadata);
        yield return CreateFinishEvent(request, id, request.Model ?? $"tavily/{model}", timestamp, usage, metadata, finishReason);
    }

    private static IEnumerable<AIStreamEvent> MapToolEvents(JsonElement tools, string provider, DateTimeOffset timestamp,
        Dictionary<string, object?> metadata, HashSet<string> inputs, HashSet<string> outputs)
    {
        var type = GetString(tools, "type");
        var key = type == "tool_call" ? "tool_call" : "tool_response";
        if (GetProperty(tools, key) is not { ValueKind: JsonValueKind.Array } array)
            yield break;
        foreach (var tool in array.EnumerateArray())
        {
            var id = GetString(tool, "id") ?? $"tool_{Guid.NewGuid():N}";
            var name = GetString(tool, "name") ?? "tool";
            var providerMetadata = ScopedMetadata(tool);
            if (type == "tool_call" && inputs.Add(id))
            {
                yield return CreateStreamEvent(provider, "tool-input-start", id, timestamp,
                    new AIToolInputStartEventData
                    { ToolName = name, Title = name, ProviderExecuted = true, ProviderMetadata = providerMetadata }, metadata);
                yield return CreateStreamEvent(provider, "tool-input-available", id, timestamp,
                    new AIToolInputAvailableEventData
                    { ToolName = name, Title = name, ProviderExecuted = true, Input = tool.Clone(), ProviderMetadata = providerMetadata }, metadata);
            }
            else if (type == "tool_response" && outputs.Add(id))
                yield return CreateStreamEvent(provider, "tool-output-available", id, timestamp,
                    new AIToolOutputAvailableEventData
                    { ToolName = name, ProviderExecuted = true, Output = tool.Clone(), ProviderMetadata = providerMetadata }, metadata);
        }
    }

    private static Dictionary<string, Dictionary<string, object>> ScopedMetadata(JsonElement? raw)
        => new() { ["tavily"] = raw is JsonElement json && json.ValueKind == JsonValueKind.Object
            ? json.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()) : [] };

    private static AIStreamEvent CreateSourceStreamEvent(string providerId, TavilySource source,
        DateTimeOffset timestamp, Dictionary<string, object?> metadata, string textId)
        => CreateStreamEvent(providerId, "source-url", textId, timestamp, new AISourceUrlEventData
        {
            SourceId = source.Url, Url = source.Url, Title = source.Title ?? source.Url,
            Type = "url_citation", ProviderMetadata = ScopedMetadata(source.Raw)
        }, metadata);

    private AIStreamEvent CreateFinishEvent(AIRequest request, string id, string model, DateTimeOffset timestamp,
        object? usage, Dictionary<string, object?> metadata, string finishReason = "stop")
        => CreateStreamEvent(GetIdentifier(), "finish", id, timestamp, new AIFinishEventData
        {
            FinishReason = finishReason, Model = model, CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(model, timestamp, usage: usage,
                temperature: request.Temperature, additionalProperties: new Dictionary<string, object?>
                { ["tavily"] = TextProviderMetadata(metadata)["tavily"] })
        }, metadata);

    private static AIStreamEvent CreateStreamEvent(string providerId, string type, string id,
        DateTimeOffset timestamp, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = new Dictionary<string, object?>(metadata)
        };

    private static bool TryGetStructuredOutputData(AIStreamEvent evt, out object? data)
    {
        data = evt.Event.Type == "data-tavily.structured-output" && evt.Event.Data is AIDataEventData part ? part.Data : null;
        return data is not null;
    }
}
