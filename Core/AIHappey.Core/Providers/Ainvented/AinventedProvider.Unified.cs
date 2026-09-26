using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Ainvented;

public sealed partial class AinventedProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = Route(request.Model);
        if (route == "workflow" || route.StartsWith("task/", StringComparison.Ordinal))
            return await ExecuteOperationAsync(request, route, cancellationToken);

        using var httpRequest = CreateRequest(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(BuildChatPayload(request, route, false).ToJsonString(Json), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var raw = doc.RootElement.Clone();
        // The standard chat mapper retains inline client function calls and multimodal inputs.
        var result = raw.ToUnifiedResponse(GetIdentifier());
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = (String(raw, "model") ?? route).ToModelId(GetIdentifier()),
            Status = result.Status, Usage = result.Usage,
            Output = AddChatOutput(result.Output, raw),
            Metadata = Metadata(raw, RequestId(response))
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = Route(request.Model);
        if (route == "workflow" || route.StartsWith("task/", StringComparison.Ordinal))
        {
            var result = await ExecuteOperationAsync(request, route, cancellationToken);
            foreach (var evt in OperationEvents(result)) yield return evt;
            yield break;
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(BuildChatPayload(request, route, true).ToJsonString(Json), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var requestId = RequestId(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var textStarted = false;
        string? textId = null;
        var tools = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        JsonElement? lastUsage = null;
        string? sessionId = null;
        string? finishReason = null;
        var model = route.ToModelId(GetIdentifier());
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            var chunk = doc.RootElement.Clone();
            var id = String(chunk, "id") ?? request.Id;
            model = (String(chunk, "model") ?? route).ToModelId(GetIdentifier());
            sessionId = String(chunk, "session_id") ?? sessionId;
            if (Property(chunk, "usage") is { ValueKind: JsonValueKind.Object } usage) lastUsage = usage;
            var metadata = Metadata(chunk, requestId);
            var choice = Property(chunk, "choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0
                ? choices[0] : default;
            if (choice.ValueKind == JsonValueKind.Object)
            {
                finishReason = String(choice, "finish_reason") ?? finishReason;
                if (Property(choice, "delta") is { ValueKind: JsonValueKind.Object } delta)
                {
                    if (String(delta, "content") is { Length: > 0 } text)
                    {
                        if (!textStarted)
                        {
                            textStarted = true;
                            textId = id;
                            yield return Event("text-start", textId, new AITextStartEventData(), metadata);
                        }
                        yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = text }, metadata);
                    }
                    if (Property(delta, "tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                        foreach (var call in calls.EnumerateArray())
                        {
                            var index = Property(call, "index")?.GetInt32() ?? 0;
                            var function = Property(call, "function");
                            if (!tools.TryGetValue(index, out var current)) current = ($"call_{index}", "", new StringBuilder());
                            current.Id = String(call, "id") ?? current.Id;
                            current.Name = function is { } fn ? String(fn, "name") ?? current.Name : current.Name;
                            if (function is { } func && String(func, "arguments") is { } args) current.Arguments.Append(args);
                            tools[index] = current;
                        }
                    if (Property(delta, "images") is { ValueKind: JsonValueKind.Array } images)
                        foreach (var image in images.EnumerateArray())
                            if (image.ValueKind == JsonValueKind.String)
                                yield return Event("file", id, new AIFileEventData { MediaType = "image/png", Url = image.GetString()!, ProviderMetadata = Scoped(chunk) }, metadata);
                    if (Property(delta, "image_meta") is { } imageMeta)
                        yield return Event("data-ainvented-image-meta", id, new AIDataEventData { Id = id, Data = imageMeta }, metadata);
                    if (Property(delta, "task_results") is { } results)
                        yield return Event("data-ainvented-task-results", id, new AIDataEventData { Id = id, Data = results }, metadata);
                }
            }
            if (Property(chunk, "context_truncation") is { } truncation)
                yield return Event("data-ainvented-context-truncation", id, new AIDataEventData { Id = id, Data = truncation }, metadata);
        }
        if (textStarted) yield return Event("text-end", textId, new AITextEndEventData());
        foreach (var (_, tool) in tools.OrderBy(pair => pair.Key))
            yield return Event("tool-input-available", tool.Id,
                new AIToolInputAvailableEventData { ToolName = tool.Name, Input = ParseArguments(tool.Arguments.ToString()), ProviderExecuted = false });
        var terminal = JsonSerializer.SerializeToElement(new { session_id = sessionId, usage = lastUsage, model, finish_reason = finishReason });
        yield return Event("finish", request.Id, new AIFinishEventData
        {
            FinishReason = finishReason ?? "stop", Model = model,
            MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow,
                lastUsage, additionalProperties: new Dictionary<string, object?> { ["ainvented"] = terminal, ["session_id"] = sessionId })
        }, Metadata(terminal, requestId));
    }

    private static object ParseArguments(string value)
    {
        try { return JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException) { return value; }
    }

    private static Dictionary<string, Dictionary<string, object>> Scoped(JsonElement raw)
        => new() { ["ainvented"] = new Dictionary<string, object> { ["raw"] = raw.Clone() } };

    private static AIOutput? AddChatOutput(AIOutput? output, JsonElement raw)
    {
        var items = output?.Items ?? [];
        if (Property(raw, "response") is { ValueKind: JsonValueKind.Object } response
            && Property(response, "images") is { ValueKind: JsonValueKind.Array } images)
            foreach (var image in images.EnumerateArray())
                if (image.ValueKind == JsonValueKind.String)
                    items.Add(new AIOutputItem
                    {
                        Type = "message", Role = "assistant",
                        Content = [new AIFileContentPart { Type = "file", MediaType = "image/png", Data = image.GetString(), Metadata = Metadata(response) }]
                    });
        return new AIOutput { Items = items, Metadata = Metadata(raw) };
    }

    private static JsonObject BuildChatPayload(AIRequest request, string route, bool stream)
    {
        var options = request.ToChatCompletionOptions("ainvented");
        var node = JsonSerializer.SerializeToNode(options, Json)!.AsObject();
        node["model"] = route;
        node["stream"] = stream;
        node.Remove("store");
        node.Remove("metadata");
        node.Remove("providerMetadata");
        node.Remove("tools");
        if (request.Tools is { Count: > 0 })
            node["tools"] = JsonSerializer.SerializeToNode(options.Tools, Json);
        var provider = Options(request);
        foreach (var name in new[] { "project_id", "session_id", "reasoning_effort", "run_workflow", "task_wait", "task_mode", "modalities", "image", "system_authority", "tools", "tool_choice" })
            if (provider.TryGetPropertyValue(name, out var value)) node[name] = value?.DeepClone();
        // An empty tools array explicitly disables auto-running workflows. Omission does not.
        if (stream) node["stream_options"] = new JsonObject { ["include_usage"] = true };
        return node;
    }
}
