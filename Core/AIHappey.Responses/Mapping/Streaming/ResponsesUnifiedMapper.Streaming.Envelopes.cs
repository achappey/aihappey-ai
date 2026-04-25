using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIEventEnvelope CreateLifecycleEnvelope(string type, int sequenceNumber, ResponseResult response, string providerId)
        => new()
        {
            Type = type,
            Output = new AIOutput { Items = [.. ToUnifiedOutputItems(response, providerId)] },
            Data = new Dictionary<string, object?>
            {
                ["sequence_number"] = sequenceNumber,
                ["response"] = response
            },
            Metadata = new Dictionary<string, object?>
            {
                ["status"] = response.Status,
                ["id"] = response.Id
            }
        };

    private static AIEventEnvelope CreateToolInputStartEnvelope(string id,
        string toolname,
        string? title = null,
        bool? providerExecuted = false)
           => new()
           {
               Type = "tool-input-start",
               Id = id,
               Data = new AIToolInputStartEventData
               {
                   ProviderExecuted = providerExecuted,
                   ToolName = toolname,
                   Title = title
               },
           };

    private static AIEventEnvelope CreateToolInputDeltaEnvelope(string id,
            string delta)
               => new()
               {
                   Type = "tool-input-delta",
                   Id = id,
                   Data = new AIToolInputDeltaEventData
                   {
                       InputTextDelta = delta
                   }
               };

    private static AIEventEnvelope CreateToolInputEndEnvelope(string id,
        string toolname,
        object input,
        string? title = null,
        bool? providerExecuted = false,
        Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    => new()
    {
        Type = "tool-input-available",
        Id = id,
        Data = new AIToolInputAvailableEventData
        {
            ProviderExecuted = providerExecuted,
            ToolName = toolname,
            Input = input,
            Title = title,
            ProviderMetadata = providerMetadata
        },
    };

    private static AIEventEnvelope CreateToolOutputEnvelope(string id,
           object output,
           string? toolName = null,
           bool? preliminary = null,
           bool? dynamic = null,
           bool? providerExecuted = false,
           Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
       => new()
       {
           Type = "tool-output-available",
           Id = id,
           Data = new AIToolOutputAvailableEventData
            {
                ProviderExecuted = providerExecuted,
                ToolName = toolName,
                Preliminary = preliminary,
                Dynamic = dynamic,
                Output = output,
               ProviderMetadata = providerMetadata,
            },
        };

    private static AIEventEnvelope CreateTextStartEnvelope(string id)
        => new()
        {
            Type = "text-start",
            Id = id,
            Data = new AITextStartEventData()
        };

    private static AIEventEnvelope CreateTextEndEnvelope(string id)
        => new()
        {
            Type = "text-end",
            Id = id,
            Data = new AITextEndEventData()
        };

    private static AIEventEnvelope CreateTextDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "text-delta",
                Id = id,
                Data = new AITextDeltaEventData
                {
                    Delta = delta
                }
            };

    private static AIEventEnvelope CreateFinishEnvelope(string id, int sequenceNumber, ResponseResult response)
    {
        var usage = response.Usage is JsonElement je ? je : default;

        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;

        if (usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var i))
                inputTokens = i.GetInt32();

            if (usage.TryGetProperty("output_tokens", out var o))
                outputTokens = o.GetInt32();

            if (usage.TryGetProperty("total_tokens", out var t))
                totalTokens = t.GetInt32();
        }

        return new()
        {
            Type = "finish",
            Id = id,
            Data = new AIFinishEventData
            {
                SequenceNumber = sequenceNumber,
                Response = response,
                Model = response.Model,
                CompletedAt = response.CompletedAt,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                FinishReason = response.Status == "failed" ? "error"
                    : response.Output.Any(a => a is ResponseFunctionCallItem) ? "tool-calls"
                    : response.Status == "completed" ? "stop"
                    : "other"
            },
            Metadata = response.Metadata
        };
    }

    private static AIEventEnvelope CreateDataEnvelope(string type, object? data)
        => new()
        {
            Type = $"data-responses.{type}",
            Data = new AIDataEventData
            {
                Data = data ?? new { }
            }
        };
}
