using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.HumanBrowser;

public partial class HumanBrowserProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = await PrepareAsync(request, cancellationToken);
        var parameters = turn.Parameters;
        var state = new TaskState();
        await foreach (var update in UpdatesAsync(turn, false, state, cancellationToken)) { }
        var metadata = Metadata(state);
        var content = new List<AIContentPart> { ReceiptPart(state, parameters, $"humanbrowser-session-{Guid.NewGuid():N}") };
        if (state.State == "input-required" && !turn.Declined) content.Add(InputForm(state));
        else if (turn.Declined) content.Add(new AITextContentPart
        {
            Type = "text",
            Text = "Input declined. The HumanBrowser task remains paused.",
            Metadata = metadata
        });
        else if (state.Answer is { Length: > 0 } answer) content.Add(new AITextContentPart
        {
            Type = "text",
            Text = answer,
            Metadata = metadata
        });
        foreach (var file in state.Files()) content.Add(file);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ModelId,
            Status = state.Failed ? "failed" : "completed",
            Usage = Usage(state),
            Metadata = metadata,
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content }], Metadata = metadata }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = await PrepareAsync(request, cancellationToken);
        var parameters = turn.Parameters;
        var state = new TaskState();
        var toolId = $"humanbrowser-session-{Guid.NewGuid():N}";
        var textId = request.Id ?? $"humanbrowser-{Guid.NewGuid():N}";
        yield return Event("tool-input-start", toolId, new AIToolInputStartEventData { ToolName = SessionToolName, Title = "HumanBrowser task", ProviderExecuted = true });
        yield return Event("tool-input-available", toolId, new AIToolInputAvailableEventData
        {
            ToolName = SessionToolName,
            Title = "HumanBrowser task",
            ProviderExecuted = true,
            Input = new { model = ModelId }
        });
        var receiptEmitted = false;
        await foreach (var update in UpdatesAsync(turn, !turn.Resume, state, cancellationToken))
        {
            var metadata = Metadata(state);
            yield return Event("data-humanbrowser", state.Id, new AIDataEventData
            {
                Id = state.Id,
                Data = JsonSerializer.SerializeToElement(update, Json),
                Transient = false
            }, metadata);
            if (!receiptEmitted && state.Id is not null)
            {
                receiptEmitted = true;
                yield return ReceiptEvent(state, parameters, toolId, true);
            }
            // Progress is not part of the final answer. Keep its full native shape as well.
            var progress = TaskState.PartText(update["status"]?["message"]?["parts"]);
            if (!string.IsNullOrWhiteSpace(progress) && !state.Stopped)
            {
                var progressId = $"{textId}-progress-{Guid.NewGuid():N}";
                yield return Event("reasoning-start", progressId, new AIReasoningStartEventData(), metadata);
                yield return Event("reasoning-delta", progressId, new AIReasoningDeltaEventData { Delta = progress }, metadata);
                yield return Event("reasoning-end", progressId, new AIReasoningEndEventData(), metadata);
            }
        }
        var terminalMetadata = Metadata(state);
        yield return ReceiptEvent(state, parameters, toolId, false);
        var form = state.State == "input-required" && !turn.Declined ? InputForm(state) : null;
        if (form is not null)
            yield return Event("tool-input-available", form.ToolCallId, new AIToolInputAvailableEventData
            {
                ToolName = form.ToolName!,
                Title = form.Title,
                Input = form.Input!,
                ProviderExecuted = false
            }, terminalMetadata);
        var answer = turn.Declined ? "Input declined. The HumanBrowser task remains paused." : state.Answer;
        if (form is null && answer.Length > 0)
        {
            var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = JsonSerializer.SerializeToElement(state.Task, Json) };
            yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = providerMetadata }, terminalMetadata);
            yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = answer, ProviderMetadata = providerMetadata }, terminalMetadata);
            yield return Event("text-end", textId, new AITextEndEventData { ProviderMetadata = providerMetadata }, terminalMetadata);
        }
        foreach (var file in state.Files())
            yield return Event("file", Guid.NewGuid().ToString("N"), new AIFileEventData
            {
                Url = (string)file.Data!,
                Filename = file.Filename,
                MediaType = file.MediaType ?? "application/octet-stream"
            }, terminalMetadata);
        var usage = Usage(state);
        yield return Event("finish", textId, new AIFinishEventData
        {
            Model = ModelId,
            FinishReason = form is not null ? "tool-calls" : state.Failed ? "error" : "stop",
            Response = JsonSerializer.SerializeToElement(state.Task, Json),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(ModelId, DateTimeOffset.UtcNow, usage,
                additionalProperties: terminalMetadata)
        }, terminalMetadata);
    }

    private async IAsyncEnumerable<JsonObject> UpdatesAsync(PreparedTurn turn, bool stream, TaskState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = turn.Parameters;
        if (turn.PausedTask is { } paused)
        {
            state.Apply(paused);
            yield return paused;
            yield break;
        }
        try
        {
            if (stream)
            {
                using var request = CreateRequest();
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Content = JsonContent.Create(Envelope("message/stream", parameters), options: Json);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(body);
                await foreach (var data in ReadSseAsync(reader, ct))
                {
                    var update = Unwrap(JsonNode.Parse(data));
                    state.Apply(update);
                    yield return update;
                    if (state.Stopped) yield break;
                }
                throw new IOException("HumanBrowser stream ended before a terminal or input-required status.");
            }
            else
            {
                var task = await RpcAsync("message/send", parameters, ct);
                state.Apply(task);
                yield return task;
                while (!state.Stopped)
                {
                    if (state.Id is null) throw new InvalidOperationException("HumanBrowser returned a task without an ID.");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    task = await RpcAsync("tasks/get", new JsonObject { ["id"] = state.Id }, ct);
                    state.Apply(task);
                    yield return task;
                }
            }
        }
        finally
        {
            // Also handles abandoned enumeration and broken upstream streams. Never cancel a
            // paused task: it must remain alive for the client's next conversational turn.
            if (!state.Stopped && state.Id is { } taskId)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await RpcAsync("tasks/cancel", new JsonObject { ["id"] = taskId }, cleanup.Token); }
                catch (Exception) { /* Best effort; preserve the original failure/cancellation. */ }
            }
        }
    }

    private async Task<JsonObject> RpcAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        using var request = CreateRequest();
        request.Content = JsonContent.Create(Envelope(method, parameters), options: Json);
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return Unwrap(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    private static object Envelope(string method, JsonObject parameters)
        => new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters };

    private static JsonObject Unwrap(JsonNode? envelope)
    {
        if (envelope is not JsonObject obj) throw new IOException("HumanBrowser returned an invalid JSON-RPC envelope.");
        if (obj["error"] is { } error) throw new InvalidOperationException($"HumanBrowser JSON-RPC error: {error.ToJsonString(Json)}");
        return obj["result"] is JsonObject result ? (JsonObject)result.DeepClone()
            : throw new IOException("HumanBrowser JSON-RPC response has no result object.");
    }

    private static async IAsyncEnumerable<string> ReadSseAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                var value = line[5..];
                data.Append(value.StartsWith(' ') ? value[1..] : value);
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }

    private static AIToolCallContentPart ReceiptPart(TaskState state, JsonObject parameters, string toolId)
    {
        var receipt = new JsonObject
        {
            ["type"] = SessionToolName,
            ["model"] = ModelId,
            ["taskId"] = state.Id,
            ["contextId"] = state.Task["contextId"]?.DeepClone(),
            ["state"] = state.State,
            ["profile"] = parameters["message"]?["metadata"]?["profile"]?.DeepClone() ?? parameters["metadata"]?["profile"]?.DeepClone(),
            ["in_reply_to"] = PendingInputId(state.Task)
        };
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = toolId,
            ToolName = SessionToolName,
            Title = "HumanBrowser task",
            ProviderExecuted = true,
            State = "output-available",
            Input = new { model = ModelId },
            Output = new { content = Array.Empty<object>(), structuredContent = receipt }
        };
    }

    private AIStreamEvent ReceiptEvent(TaskState state, JsonObject parameters, string toolId, bool preliminary)
        => Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = SessionToolName,
            Output = ReceiptPart(state, parameters, toolId).Output!,
            ProviderExecuted = true,
            Dynamic = true,
            Preliminary = preliminary
        }, Metadata(state));

    private static Dictionary<string, object?> Metadata(TaskState state)
        => new() { ["humanbrowser"] = JsonSerializer.SerializeToElement(state.Task, Json) };

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata = null)
        => new() { ProviderId = GetIdentifier(), Metadata = metadata, Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow } };

    private static AIUsage Usage(TaskState state)
    {
        var cost = state.Task["metadata"]?["cost"];
        int? Number(string key) => cost?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
        var input = Number("tokens_in");
        var output = Number("tokens_out");
        return new AIUsage { InputTokens = input, OutputTokens = output, TotalTokens = input.HasValue && output.HasValue ? input + output : null };
    }

    private static ChatCompletionUpdate ToChatUpdate(AIStreamEvent evt, string model)
    {
        object? delta = evt.Event.Data switch
        {
            AITextDeltaEventData text => new { content = text.Delta },
            AIReasoningDeltaEventData progress => new { reasoning_content = progress.Delta },
            AIToolInputAvailableEventData input => new { tool_calls = new[] { new { index = input.ProviderExecuted == true ? 0 : 1, id = evt.Event.Id, type = "function", provider_executed = input.ProviderExecuted, function = new { name = input.ToolName, arguments = JsonSerializer.Serialize(input.Input, Json) } } } },
            AIToolOutputAvailableEventData output => new { tool_calls = new[] { new { index = 0, id = evt.Event.Id, type = "function", provider_executed = true, function = new { name = output.ToolName, output = output.Output } } } },
            _ => null
        };
        var finish = evt.Event.Data as AIFinishEventData;
        return new ChatCompletionUpdate
        {
            Id = evt.Event.Id ?? "humanbrowser",
            Object = "chat.completion.chunk",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices = delta is not null || finish is not null ? [new { index = 0, delta = delta ?? new { }, finish_reason = finish?.FinishReason == "tool-calls" ? "tool_calls" : finish?.FinishReason }] : [],
            Usage = finish is null ? null : new { prompt_tokens = finish.InputTokens, completion_tokens = finish.OutputTokens, total_tokens = finish.TotalTokens },
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["provider_metadata"] = JsonSerializer.SerializeToElement(evt.Metadata)
            }
        };
    }

    private sealed class TaskState
    {
        public JsonObject Task { get; } = new();
        public string? Id => Text(Task, "id");
        public string? State => Text(Task["status"], "state");
        public bool Stopped => State is "completed" or "failed" or "canceled" or "rejected" or "input-required" or "auth-required";
        public bool Failed => State is "failed" or "canceled" or "rejected";

        public void Apply(JsonObject update)
        {
            var kind = Text(update, "kind");
            if (kind is "task" or null) Merge(Task, update);
            else
            {
                Task["id"] ??= update["taskId"]?.DeepClone();
                Task["contextId"] ??= update["contextId"]?.DeepClone();
                if (update["metadata"] is JsonObject metadata)
                {
                    var current = Task["metadata"] as JsonObject ?? new JsonObject();
                    Merge(current, metadata);
                    Task["metadata"] = current.DeepClone();
                }
                if (update["status"] is JsonObject status) Task["status"] = status.DeepClone();
                if (update["artifact"] is JsonObject artifact)
                {
                    var artifacts = Task["artifacts"] as JsonArray ?? new JsonArray();
                    var old = artifacts.OfType<JsonObject>().FirstOrDefault(a => Text(a, "artifactId") == Text(artifact, "artifactId"));
                    if (old is not null && update["append"]?.GetValue<bool>() == true)
                    {
                        var parts = old["parts"] as JsonArray ?? new JsonArray();
                        foreach (var part in artifact["parts"] as JsonArray ?? []) parts.Add(part?.DeepClone());
                        old["parts"] = parts.DeepClone();
                    }
                    else
                    {
                        if (old is not null) artifacts.Remove(old);
                        artifacts.Add(artifact.DeepClone());
                    }
                    Task["artifacts"] = artifacts.DeepClone();
                }
            }
        }

        public string Answer
        {
            get
            {
                var artifacts = string.Join("\n", (Task["artifacts"] as JsonArray ?? []).Select(a => PartText(a?["parts"])).Where(t => t.Length > 0));
                if (State == "input-required") return Text(Task["metadata"]?["input_required"], "question") ?? PartText(Task["status"]?["message"]?["parts"]);
                if (artifacts.Length > 0) return artifacts;
                return Text(Task["metadata"]?["outcome"], "result") ?? PartText(Task["status"]?["message"]?["parts"]);
            }
        }

        public static string PartText(JsonNode? parts)
            => string.Concat((parts as JsonArray ?? []).Where(p => Text(p, "kind") == "text").Select(p => Text(p, "text")));

        public IEnumerable<AIFileContentPart> Files()
        {
            foreach (var artifact in Task["artifacts"] as JsonArray ?? [])
                foreach (var part in artifact?["parts"] as JsonArray ?? [])
                    if (Text(part, "kind") == "file" && part?["file"] is JsonObject file)
                    {
                        var data = Text(file, "uri") ?? (Text(file, "bytes") is { } bytes ? $"data:{Text(file, "mimeType") ?? "application/octet-stream"};base64,{bytes}" : null);
                        if (data is not null) yield return new AIFileContentPart
                        {
                            Type = "file",
                            Filename = Text(file, "name"),
                            MediaType = Text(file, "mimeType"),
                            Data = data
                        };
                    }
        }
    }
}
