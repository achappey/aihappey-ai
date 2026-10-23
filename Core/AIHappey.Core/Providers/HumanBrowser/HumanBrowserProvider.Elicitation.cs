using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.HumanBrowser;

public partial class HumanBrowserProvider
{
    private const string FormPrefix = "humanbrowser-input-";

    private static string FormId(string taskId, string requestId)
        => FormPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId + "\n" + requestId)));

    private static AIToolCallContentPart InputForm(TaskState state)
    {
        var taskId = state.Id ?? throw new InvalidOperationException("HumanBrowser input-required task omitted its ID.");
        var requestId = PendingInputId(state.Task) ?? throw new InvalidOperationException("HumanBrowser input-required task omitted the pending request ID.");
        var elicitation = new ElicitRequestParams
        {
            Mode = "form",
            Message = string.IsNullOrWhiteSpace(state.Answer) ? "HumanBrowser needs your input to continue." : state.Answer,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["answer"] = new ElicitRequestParams.StringSchema { Title = "Your answer", MinLength = 1, MaxLength = 20000 },
                    ["sensitive"] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = "Sensitive value",
                        Description = "Enable for passwords, tokens, or one-time codes. Sent as a sensitive DataPart, not goal text."
                    }
                },
                Required = ["answer"]
            }
        };
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = FormId(taskId, requestId),
            ToolName = "ai_input_required",
            Title = "Input required",
            Input = InputRequest.ForElicitation(elicitation),
            State = "input-available",
            ProviderExecuted = false,
            Metadata = Metadata(state)
        };
    }

    private static AIToolCallContentPart? FindFormAnswer(AIRequest request)
    {
        var parts = (request.Input?.Items ?? []).SelectMany(i => i.Content ?? []).ToList();
        var lastReceipt = parts.FindLastIndex(p => p is AIToolCallContentPart t && t.ToolName == SessionToolName);
        var lastUserTextItem = (request.Input?.Items ?? []).FindLastIndex(i => i.Role == "user" && (i.Content ?? []).Any(p => p is AITextContentPart));
        var offset = lastUserTextItem < 0 ? 0 : (request.Input?.Items ?? []).Take(lastUserTextItem).Sum(i => i.Content?.Count ?? 0);
        // Earlier answered forms are history, not instructions to submit again.
        return parts.Skip(Math.Max(lastReceipt, offset)).OfType<AIToolCallContentPart>().LastOrDefault(t =>
            t.ToolCallId.StartsWith(FormPrefix, StringComparison.Ordinal) && t.Output is not null);
    }

    private static JsonObject? ElicitationResult(object? output)
    {
        var node = JsonSerializer.SerializeToNode(output, Json);
        for (var depth = 0; depth < 8; depth++)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) node = JsonNode.Parse(text);
            else if (node is JsonObject obj && obj["structuredContent"] is { } structured) node = structured;
            else return node as JsonObject;
        }
        throw new ArgumentException("Invalid HumanBrowser elicitation result.");
    }
}
