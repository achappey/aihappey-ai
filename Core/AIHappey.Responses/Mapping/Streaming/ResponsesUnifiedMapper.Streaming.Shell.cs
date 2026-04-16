using System.Text;
using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly AsyncLocal<ResponseShellStreamState?> CurrentShellStreamState = new();

    private static ResponseShellStreamState GetShellStreamState()
        => CurrentShellStreamState.Value ??= new ResponseShellStreamState();

    private static void ClearShellStreamState()
        => CurrentShellStreamState.Value = null;

    private static void RegisterShellCall(ResponseStreamItem item, int outputIndex)
    {
        var state = GetShellStreamState();
        var callId = TryGetItemString(item, "call_id");

        var accumulator = new ShellInputAccumulator
        {
            ItemId = item.Id ?? string.Empty,
            OutputIndex = outputIndex,
            CallId = callId
        };

        state.InputsByOutputIndex[outputIndex] = accumulator;

        if (!string.IsNullOrWhiteSpace(callId))
        {
            state.InputsByCallId[callId] = accumulator;

            if (state.OutputsByCallId.TryGetValue(callId, out var outputAccumulator))
                outputAccumulator.ToolItemId = accumulator.ItemId;
        }
    }

    private static void RegisterShellCallOutput(ResponseStreamItem item, int outputIndex)
    {
        var state = GetShellStreamState();
        var callId = TryGetItemString(item, "call_id");

        ShellOutputAccumulator accumulator;

        if (!string.IsNullOrWhiteSpace(callId) && state.OutputsByCallId.TryGetValue(callId, out var existing))
        {
            accumulator = existing;
        }
        else
        {
            accumulator = new ShellOutputAccumulator();
        }

        accumulator.OutputIndex = outputIndex;
        accumulator.OutputItemId = item.Id ?? accumulator.OutputItemId;
        accumulator.CallId = callId ?? accumulator.CallId;
        accumulator.Status = item.Status ?? accumulator.Status;
        accumulator.MaxOutputLength = TryGetItemInt(item, "max_output_length") ?? accumulator.MaxOutputLength;

        if (!string.IsNullOrWhiteSpace(callId) && state.InputsByCallId.TryGetValue(callId, out var inputAccumulator))
            accumulator.ToolItemId = inputAccumulator.ItemId;

        state.OutputsByOutputIndex[outputIndex] = accumulator;

        if (!string.IsNullOrWhiteSpace(accumulator.CallId))
            state.OutputsByCallId[accumulator.CallId] = accumulator;
    }

    private static IEnumerable<AIEventEnvelope> CreatePendingShellCompletionEnvelopes(
        string providerId,
        ResponseResult response)
    {
        var completedItems = new List<ResponseStreamItem>();

        foreach (var rawItem in response.Output ?? [])
        {
            if (rawItem is null)
                continue;

            ResponseStreamItem? item;

            try
            {
                item = rawItem as ResponseStreamItem
                    ?? JsonSerializer.Deserialize<ResponseStreamItem>(JsonSerializer.Serialize(rawItem, Json), Json);
            }
            catch
            {
                item = null;
            }

            if (item is null)
                continue;

            completedItems.Add(item);
        }

        foreach (var item in completedItems)
        {
            if (string.Equals(item.Type, "shell_call", StringComparison.OrdinalIgnoreCase))
                HydrateShellCallFromCompletedResponse(item);
        }

        foreach (var item in completedItems)
        {

            if (string.Equals(item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase))
            {
                var outputIndex = TryGetItemInt(item, "output_index") ?? -1;
                foreach (var env in CreateShellToolOutputFinalEnvelopes(providerId, item, outputIndex))
                    yield return env;
            }
        }
    }

    private static void HydrateShellCallFromCompletedResponse(ResponseStreamItem item)
    {
        var state = GetShellStreamState();
        var callId = TryGetItemString(item, "call_id");
        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(item.Id))
            return;

        var accumulator = state.InputsByCallId.TryGetValue(callId, out var existing)
            ? existing
            : FindShellInputAccumulatorByItemId(state, item.Id!);

        if (accumulator is null)
        {
            accumulator = new ShellInputAccumulator
            {
                ItemId = item.Id!,
                OutputIndex = TryGetItemInt(item, "output_index") ?? -1,
                CallId = callId
            };

            state.InputsByOutputIndex[accumulator.OutputIndex] = accumulator;
        }
        else
        {
            accumulator.CallId = callId;
        }

        var commands = ExtractShellCommands(item);
        if (commands.Count > 0)
            SyncShellCommands(accumulator, commands);

        state.InputsByCallId[callId] = accumulator;

        if (state.OutputsByCallId.TryGetValue(callId, out var outputAccumulator))
            outputAccumulator.ToolItemId = accumulator.ItemId;
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolInputDeltaEnvelopes(ResponseShellCallCommandAdded part)
    {
        var accumulator = ResolveShellInputAccumulator(part.OutputIndex);
        if (accumulator is null)
            yield break;

        if (!accumulator.JsonStarted)
        {
            accumulator.JsonStarted = true;
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, "{\"commands\":[");
        }
        else if (part.CommandIndex > 0)
        {
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, ",");
        }

        yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, "\"");

        if (!string.IsNullOrEmpty(part.Command))
        {
            GetShellCommandBuilder(accumulator, part.CommandIndex).Append(part.Command);
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, EscapeJsonStringFragment(part.Command));
        }
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolInputDeltaEnvelopes(ResponseShellCallCommandDelta part)
    {
        var accumulator = ResolveShellInputAccumulator(part.OutputIndex);
        if (accumulator is null)
            yield break;

        GetShellCommandBuilder(accumulator, part.CommandIndex).Append(part.Delta);
        yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, EscapeJsonStringFragment(part.Delta));
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolInputDeltaEnvelopes(ResponseShellCallCommandDone part)
    {
        var accumulator = ResolveShellInputAccumulator(part.OutputIndex);
        if (accumulator is null)
            yield break;

        var builder = GetShellCommandBuilder(accumulator, part.CommandIndex);
        var current = builder.ToString();
        var completed = part.Command ?? string.Empty;

        if (completed.Length > current.Length && completed.StartsWith(current, StringComparison.Ordinal))
        {
            var missing = completed[current.Length..];
            builder.Append(missing);

            if (!string.IsNullOrEmpty(missing))
                yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, EscapeJsonStringFragment(missing));
        }
        else if (current.Length == 0 && !string.IsNullOrEmpty(completed))
        {
            builder.Append(completed);
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, EscapeJsonStringFragment(completed));
        }

        yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, "\"");
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolInputAvailableEnvelopes(ResponseStreamItem item, int outputIndex)
    {
        var state = GetShellStreamState();
        var callId = TryGetItemString(item, "call_id");

        var accumulator = ResolveShellInputAccumulator(outputIndex)
            ?? (!string.IsNullOrWhiteSpace(callId) && state.InputsByCallId.TryGetValue(callId, out var byCallId)
                ? byCallId
                : null);

        if (accumulator is null)
        {
            accumulator = new ShellInputAccumulator
            {
                ItemId = item.Id ?? string.Empty,
                OutputIndex = outputIndex,
                CallId = callId
            };

            state.InputsByOutputIndex[outputIndex] = accumulator;

            if (!string.IsNullOrWhiteSpace(callId))
                state.InputsByCallId[callId] = accumulator;
        }

        var commands = ExtractShellCommands(item);
        if (commands.Count == 0)
            commands = [.. accumulator.CommandBuilders.Select(builder => builder.ToString())];
        else
            SyncShellCommands(accumulator, commands);

        var input = CreateShellCallInput(commands);

        if (!accumulator.JsonStarted)
        {
            accumulator.JsonStarted = true;
            accumulator.JsonClosed = true;
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, input.GetRawText());
        }
        else if (!accumulator.JsonClosed)
        {
            accumulator.JsonClosed = true;
            yield return CreateToolInputDeltaEnvelope(accumulator.ItemId, "]}");
        }

        yield return CreateToolInputEndEnvelope(
            accumulator.ItemId,
            "shell_call",
            input,
            "shell_call",
            providerExecuted: true);
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolOutputPreliminaryEnvelopes(
        string providerId,
        ResponseUnknownEvent unknown,
        bool isCompletedChunk)
    {
        if (!TryGetUnknownEventProperty(unknown, "output_index", out var outputIndexElement)
            || !TryGetInt32(outputIndexElement, out var outputIndex))
        {
            yield break;
        }

        var state = GetShellStreamState();
        var accumulator = ResolveShellOutputAccumulator(outputIndex);

        if (accumulator is null)
        {
            accumulator = new ShellOutputAccumulator
            {
                OutputIndex = outputIndex,
                OutputItemId = TryGetUnknownEventString(unknown, "item_id")
            };

            state.OutputsByOutputIndex[outputIndex] = accumulator;
        }

        if (TryGetUnknownEventProperty(unknown, "command_index", out var commandIndexElement)
            && TryGetInt32(commandIndexElement, out var commandIndex))
        {
            var chunkAccumulator = GetShellOutputChunkAccumulator(accumulator, commandIndex);

            if (!isCompletedChunk)
            {
                if (TryGetUnknownEventProperty(unknown, "delta", out var deltaElement)
                    && deltaElement.ValueKind == JsonValueKind.Object)
                {
                    AppendShellOutputChunkDelta(chunkAccumulator, deltaElement);
                }
            }
            else if (TryGetUnknownEventProperty(unknown, "output", out var outputElement)
                     && outputElement.ValueKind == JsonValueKind.Array)
            {
                ApplyShellOutputChunks(accumulator, outputElement, commandIndex);
            }
        }

        if (string.IsNullOrWhiteSpace(accumulator.ToolItemId)
            && !string.IsNullOrWhiteSpace(accumulator.CallId)
            && state.InputsByCallId.TryGetValue(accumulator.CallId, out var inputAccumulator))
        {
            accumulator.ToolItemId = inputAccumulator.ItemId;
        }

        if (string.IsNullOrWhiteSpace(accumulator.ToolItemId))
            yield break;

        yield return CreateToolOutputEnvelope(
            accumulator.ToolItemId,
            CreateShellCallToolResult(accumulator),
            preliminary: true,
            providerExecuted: true,
            providerMetadata: CreateShellToolOutputProviderMetadata(providerId, accumulator.ToolItemId, accumulator.CallId, accumulator.OutputItemId));
    }

    private static IEnumerable<AIEventEnvelope> CreateShellToolOutputFinalEnvelopes(
        string providerId,
        ResponseStreamItem item,
        int outputIndex)
    {
        var state = GetShellStreamState();
        var callId = TryGetItemString(item, "call_id");

        var accumulator = ResolveShellOutputAccumulator(outputIndex)
            ?? (!string.IsNullOrWhiteSpace(callId) && state.OutputsByCallId.TryGetValue(callId, out var byCallId)
                ? byCallId
                : null)
            ?? new ShellOutputAccumulator();

        accumulator.OutputIndex = outputIndex;
        accumulator.OutputItemId = item.Id ?? accumulator.OutputItemId;
        accumulator.CallId = callId ?? accumulator.CallId;
        accumulator.Status = item.Status ?? accumulator.Status;
        accumulator.MaxOutputLength = TryGetItemInt(item, "max_output_length") ?? accumulator.MaxOutputLength;

        if (!string.IsNullOrWhiteSpace(accumulator.CallId) && state.InputsByCallId.TryGetValue(accumulator.CallId, out var inputAccumulator))
            accumulator.ToolItemId = inputAccumulator.ItemId;

        if (TryGetItemJson(item, "output", out var outputElement) && outputElement.ValueKind == JsonValueKind.Array)
            ApplyShellOutputChunks(accumulator, outputElement);

        if (accumulator.FinalOutputEmitted)
            yield break;

        if (string.IsNullOrWhiteSpace(accumulator.ToolItemId))
            yield break;

        accumulator.FinalOutputEmitted = true;

        yield return CreateToolOutputEnvelope(
            accumulator.ToolItemId,
            CreateShellCallToolResult(accumulator),
            providerExecuted: true,
            providerMetadata: CreateShellToolOutputProviderMetadata(providerId, accumulator.ToolItemId, accumulator.CallId, accumulator.OutputItemId));

        state.OutputsByOutputIndex.Remove(outputIndex);

        if (!string.IsNullOrWhiteSpace(accumulator.CallId))
        {
            state.OutputsByCallId.Remove(accumulator.CallId);
            state.InputsByCallId.Remove(accumulator.CallId);
        }

        if (state.InputsByOutputIndex.TryGetValue(outputIndex, out var shellInput)
            && string.Equals(shellInput.ItemId, accumulator.ToolItemId, StringComparison.Ordinal))
        {
            state.InputsByOutputIndex.Remove(outputIndex);
        }
    }

    private static ShellInputAccumulator? ResolveShellInputAccumulator(int outputIndex)
    {
        var state = GetShellStreamState();
        return state.InputsByOutputIndex.TryGetValue(outputIndex, out var accumulator)
            ? accumulator
            : null;
    }

    private static ShellInputAccumulator? FindShellInputAccumulatorByItemId(
        ResponseShellStreamState state,
        string itemId)
        => state.InputsByOutputIndex.Values.FirstOrDefault(accumulator => string.Equals(accumulator.ItemId, itemId, StringComparison.Ordinal));

    private static ShellOutputAccumulator? ResolveShellOutputAccumulator(int outputIndex)
    {
        var state = GetShellStreamState();
        return state.OutputsByOutputIndex.TryGetValue(outputIndex, out var accumulator)
            ? accumulator
            : null;
    }

    private static StringBuilder GetShellCommandBuilder(ShellInputAccumulator accumulator, int commandIndex)
    {
        while (accumulator.CommandBuilders.Count <= commandIndex)
            accumulator.CommandBuilders.Add(new StringBuilder());

        return accumulator.CommandBuilders[commandIndex];
    }

    private static ShellOutputChunkAccumulator GetShellOutputChunkAccumulator(ShellOutputAccumulator accumulator, int commandIndex)
    {
        if (!accumulator.Chunks.TryGetValue(commandIndex, out var chunkAccumulator))
        {
            chunkAccumulator = new ShellOutputChunkAccumulator();
            accumulator.Chunks[commandIndex] = chunkAccumulator;
        }

        return chunkAccumulator;
    }

    private static void AppendShellOutputChunkDelta(ShellOutputChunkAccumulator accumulator, JsonElement delta)
    {
        if (delta.TryGetProperty("stdout", out var stdout))
            accumulator.Stdout.Append(stdout.GetString() ?? stdout.ToString());

        if (delta.TryGetProperty("stderr", out var stderr))
            accumulator.Stderr.Append(stderr.GetString() ?? stderr.ToString());

        if (delta.TryGetProperty("created_by", out var createdBy))
            accumulator.CreatedBy = createdBy.GetString() ?? createdBy.ToString();
    }

    private static void ApplyShellOutputChunks(ShellOutputAccumulator accumulator, JsonElement output, int? startIndex = null)
    {
        if (output.ValueKind != JsonValueKind.Array)
            return;

        var index = 0;

        foreach (var chunk in output.EnumerateArray())
        {
            var commandIndex = startIndex.HasValue && output.GetArrayLength() == 1
                ? startIndex.Value
                : index;

            var chunkAccumulator = GetShellOutputChunkAccumulator(accumulator, commandIndex);
            chunkAccumulator.Stdout.Clear();
            chunkAccumulator.Stderr.Clear();

            if (chunk.TryGetProperty("stdout", out var stdout))
                chunkAccumulator.Stdout.Append(stdout.GetString() ?? stdout.ToString());

            if (chunk.TryGetProperty("stderr", out var stderr))
                chunkAccumulator.Stderr.Append(stderr.GetString() ?? stderr.ToString());

            if (chunk.TryGetProperty("created_by", out var createdBy))
                chunkAccumulator.CreatedBy = createdBy.GetString() ?? createdBy.ToString();

            chunkAccumulator.Outcome = chunk.TryGetProperty("outcome", out var outcome)
                ? outcome.Clone()
                : null;
            chunkAccumulator.IsCompleted = true;
            index++;
        }
    }

    private static CallToolResult CreateShellCallToolResult(ShellOutputAccumulator accumulator)
        => new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["call_id"] = accumulator.CallId,
                    ["status"] = accumulator.Status,
                    ["max_output_length"] = accumulator.MaxOutputLength,
                    ["output"] = accumulator.Chunks
                        .OrderBy(chunk => chunk.Key)
                        .Select(chunk => new Dictionary<string, object?>
                        {
                            ["stdout"] = chunk.Value.Stdout.ToString(),
                            ["stderr"] = chunk.Value.Stderr.ToString(),
                            ["created_by"] = chunk.Value.CreatedBy,
                            ["outcome"] = chunk.Value.Outcome
                        })
                        .ToList()
                },
                JsonSerializerOptions.Web)
        };

    private static JsonElement CreateShellCallInput(IReadOnlyList<string> commands)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["commands"] = commands
            },
            JsonSerializerOptions.Web);

    private static List<string> ExtractShellCommands(ResponseStreamItem item)
    {
        if (!TryGetItemJson(item, "action", out var action)
            || action.ValueKind != JsonValueKind.Object
            || !action.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. commands.EnumerateArray().Select(command => command.GetString() ?? command.ToString())];
    }

    private static void SyncShellCommands(ShellInputAccumulator accumulator, IReadOnlyList<string> commands)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            var builder = GetShellCommandBuilder(accumulator, i);
            builder.Clear();
            builder.Append(commands[i]);
        }
    }

    private static string EscapeJsonStringFragment(string value)
    {
        var encoded = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
        return encoded.Length >= 2 ? encoded[1..^1] : string.Empty;
    }

    private static bool TryGetItemJson(ResponseStreamItem item, string key, out JsonElement value)
    {
        if (item.AdditionalProperties?.TryGetValue(key, out value) == true)
        {
            value = value.Clone();
            return true;
        }

        value = default;
        return false;
    }

    private static string? TryGetItemString(ResponseStreamItem item, string key)
        => item.AdditionalProperties?.TryGetValue(key, out var value) == true
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int? TryGetItemInt(ResponseStreamItem item, string key)
        => item.AdditionalProperties?.TryGetValue(key, out var value) == true && TryGetInt32(value, out var number)
            ? number
            : null;

    private static Dictionary<string, Dictionary<string, object>> CreateShellToolOutputProviderMetadata(
        string providerId,
        string toolUseId,
        string? callId,
        string? outputItemId)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "tool_result",
                ["tool_name"] = "shell_call",
                ["title"] = "shell_call",
                ["tool_use_id"] = toolUseId,
                ["call_id"] = callId ?? string.Empty,
                ["output_item_id"] = outputItemId ?? string.Empty
            }
        };

    private sealed class ResponseShellStreamState
    {
        public Dictionary<int, ShellInputAccumulator> InputsByOutputIndex { get; } = [];

        public Dictionary<string, ShellInputAccumulator> InputsByCallId { get; } = new(StringComparer.Ordinal);

        public Dictionary<int, ShellOutputAccumulator> OutputsByOutputIndex { get; } = [];

        public Dictionary<string, ShellOutputAccumulator> OutputsByCallId { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ShellInputAccumulator
    {
        public string ItemId { get; init; } = string.Empty;

        public int OutputIndex { get; init; }

        public string? CallId { get; set; }

        public List<StringBuilder> CommandBuilders { get; } = [];

        public bool JsonStarted { get; set; }

        public bool JsonClosed { get; set; }
    }

    private sealed class ShellOutputAccumulator
    {
        public int OutputIndex { get; set; }

        public string? OutputItemId { get; set; }

        public string? ToolItemId { get; set; }

        public string? CallId { get; set; }

        public string? Status { get; set; }

        public int? MaxOutputLength { get; set; }

        public bool FinalOutputEmitted { get; set; }

        public SortedDictionary<int, ShellOutputChunkAccumulator> Chunks { get; } = [];
    }

    private sealed class ShellOutputChunkAccumulator
    {
        public StringBuilder Stdout { get; } = new();

        public StringBuilder Stderr { get; } = new();

        public string? CreatedBy { get; set; }

        public JsonElement? Outcome { get; set; }

        public bool IsCompleted { get; set; }
    }
}
