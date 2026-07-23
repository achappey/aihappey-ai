using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly AsyncLocal<HashSet<string>?> CurrentCodeInterpreterTerminalOutputs = new();

    private static HashSet<string> GetCodeInterpreterTerminalOutputs()
        => CurrentCodeInterpreterTerminalOutputs.Value ??= new HashSet<string>(StringComparer.Ordinal);

    private static void ClearCodeInterpreterStreamState()
        => CurrentCodeInterpreterTerminalOutputs.Value = null;

    private static IEnumerable<AIEventEnvelope> CreateCodeInterpreterOutputEnvelopes(
        string providerId,
        ResponseOutputItemDone done,
        bool authoritative)
    {
        var itemId = done.Item.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemId))
            yield break;

        var status = done.Item.Status;
        var isCompleted = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
        if (authoritative && !isCompleted)
            yield break;

        var terminalOutputs = GetCodeInterpreterTerminalOutputs();
        if (isCompleted && !terminalOutputs.Add(itemId))
            yield break;

        JsonElement? outputs = done.Item.AdditionalProperties?.TryGetValue("outputs", out var output) == true
            ? output.Clone()
            : null;
        var containerId = done.Item.AdditionalProperties?.TryGetValue("container_id", out var container) == true
            ? container.ToString()
            : string.Empty;
        var caller = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "caller");
        var providerMetadata = CreateProviderMetadata(providerId, new Dictionary<string, object?>
        {
            ["type"] = "code_interpreter_call",
            ["id"] = itemId,
            ["item_id"] = itemId,
            ["status"] = status,
            ["container_id"] = containerId,
            ["caller"] = caller,
            ["output_index"] = done.OutputIndex,
            ["authoritative_completion"] = authoritative
        });

        yield return CreateToolOutputEnvelope(
            itemId,
            new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    container_id = containerId,
                    outputs
                })
            },
            toolName: "code_interpreter",
            preliminary: isCompleted ? false : true,
            providerExecuted: true,
            providerMetadata: providerMetadata);

        foreach (var fileEnvelope in CreateCodeInterpreterOutputFileEnvelopes(providerId, done))
            yield return fileEnvelope;
    }

    private static IEnumerable<AIEventEnvelope> CreatePendingCodeInterpreterCompletionEnvelopes(
        string providerId,
        ResponseResult response)
    {
        var outputIndex = 0;
        foreach (var rawItem in response.Output ?? [])
        {
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

            if (item is not null
                && string.Equals(item.Type, "code_interpreter_call", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var done = new ResponseOutputItemDone
                {
                    Item = item,
                    OutputIndex = outputIndex
                };

                foreach (var envelope in CreateCodeInterpreterOutputEnvelopes(providerId, done, authoritative: true))
                    yield return envelope;
            }

            outputIndex++;
        }
    }
}
