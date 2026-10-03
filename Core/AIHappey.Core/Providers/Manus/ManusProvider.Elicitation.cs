using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;
using NJsonSchema;

namespace AIHappey.Core.Providers.Manus;

public sealed partial class ManusProvider
{
    private static bool IsQuestion(string? type) => type is "messageAskUser" or "cascadeAskUser";
    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal)
    {
        "needConnectMyBrowser", "gmailSendAction", "outlookSendMailsAction", "deployAction", "terminalExecute", "videoGenerate",
        "apiHighCreditNotice", "googleCalendarCreate", "googleCalendarUpdate", "googleCalendarDelete", "outlookCalendarCreate",
        "outlookCalendarUpdate", "outlookCalendarDelete", "shopifyAction", "instagramCreateResult", "metaMarketingAction",
        "metaMarketingActionResult", "webdevRunAction", "webdevRequestSecrets", "mapreduceAction"
    };
    private static string FormId(Execution e) => FormPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        e.TaskId + "\n" + S(e.Waiting, "waiting_for_event_id") + "\n" + S(e.Waiting, "waiting_for_event_type"))));

    private static async Task<JsonElement> ConfirmationSchema(Execution e, CancellationToken ct)
    {
        if (!ActionTypes.Contains(S(e.Waiting, "waiting_for_event_type") ?? ""))
            throw new NotSupportedException("This Manus waiting state requires the Manus UI or external authorization.");
        if (P(e.Waiting, "confirm_input_schema") is { ValueKind: JsonValueKind.Object } schema) return schema;
        // Only explicitly documented inputs. Never infer a generic accept/cancel contract.
        return S(e.Waiting, "waiting_for_event_type") switch
        {
            "gmailSendAction" or "outlookSendMailsAction" => El(new { type = "object", properties = new { accept = new { type = "boolean" }, save_draft = new { type = "boolean" } }, required = new[] { "accept" }, additionalProperties = false }),
            "deployAction" or "terminalExecute" or "googleCalendarCreate" or "googleCalendarUpdate" or "googleCalendarDelete"
                or "outlookCalendarCreate" or "outlookCalendarUpdate" or "outlookCalendarDelete" or "shopifyAction"
                or "instagramCreateResult" or "mapreduceAction" or "metaMarketingActionResult" => El(new { type = "object", properties = new { accept = new { type = "boolean" } }, required = new[] { "accept" }, additionalProperties = false }),
            "videoGenerate" => El(new { type = "object", properties = new { choice = new { type = "string", @enum = new[] { "standard", "premium" } } }, required = new[] { "choice" }, additionalProperties = false }),
            "apiHighCreditNotice" => El(new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "accept", "reject", "do_not_show_again" } } }, required = new[] { "action" }, additionalProperties = false }),
            _ => await UnsupportedSchema(ct)
        };
    }
    private static Task<JsonElement> UnsupportedSchema(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException("Manus did not expose a safe confirmation schema for this action. Continue in the Manus UI.");
    }
    private static bool PrimitiveSchema(JsonElement schema)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "type", "title", "description", "enum", "minimum", "maximum", "minLength", "maxLength", "format", "default" };
        return S(schema, "type") is "string" or "boolean" or "number" or "integer"
            && schema.EnumerateObject().All(p => allowed.Contains(p.Name))
            && (P(schema, "enum") is null || S(schema, "type") == "string")
            && (S(schema, "type") != "integer" || P(schema, "default") is null);
    }
    private static bool FlatSchema(JsonElement schema)
        => S(schema, "type") == "object" && P(schema, "properties") is { ValueKind: JsonValueKind.Object } properties
            && properties.EnumerateObject().All(p => PrimitiveSchema(p.Value))
            && schema.EnumerateObject().All(p => p.Name is "type" or "properties" or "required" or "additionalProperties" or "title" or "description")
            && P(schema, "additionalProperties")?.ValueKind is null or JsonValueKind.False;

    private static ElicitRequestParams.RequestSchema ConvertSchema(JsonElement schema)
    {
        if (!FlatSchema(schema))
            return new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["input_json"] = new ElicitRequestParams.StringSchema
                    {
                        Title = "Action input (JSON)",
                        Description = "Provide a JSON object matching the exact schema: " + schema.GetRawText(),
                        MinLength = 2,
                        MaxLength = 120 * 1024
                    }
                },
                Required = ["input_json"]
            };
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            var raw = property.Value;
            // The SDK polymorphic converter retains its supported primitive constraints.
            var definition = raw.Deserialize<ElicitRequestParams.PrimitiveSchemaDefinition>(Json)
                ?? throw new InvalidOperationException("Invalid elicitation primitive schema.");
            properties[property.Name] = definition;
        }
        return new ElicitRequestParams.RequestSchema { Properties = properties, Required = Array(schema, "required").Select(v => v.GetString()!).ToList() };
    }
    private static async Task<AIToolCallContentPart> WaitingForm(Execution e, CancellationToken ct)
    {
        var type = S(e.Waiting, "waiting_for_event_type");
        var id = S(e.Waiting, "waiting_for_event_id");
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Manus waiting state omitted event identity; refresh the task.");
        var message = S(e.Waiting, "waiting_description") ?? "Manus needs your input.";
        ElicitRequestParams elicitation;
        if (IsQuestion(type))
        {
            var question = P(e.Question, "assistant_message") ?? Empty;
            var expectation = P(question, "question_expectation") ?? Empty;
            if (S(expectation, "response_method") is { } method && method != "send_message")
                throw new NotSupportedException("Unknown Manus question response method.");
            var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            { ["answer"] = new ElicitRequestParams.StringSchema { Title = "Your answer", Description = "Free-text answers are allowed.", MaxLength = 20000 } };
            var choices = Array(expectation, "options").Select(v => v.GetString()!).ToList();
            if (choices.Count > 0)
            {
                if (S(expectation, "selection_mode") == "multiple")
                    properties["choices"] = new ElicitRequestParams.UntitledMultiSelectEnumSchema
                    { Title = "Select options", Items = new ElicitRequestParams.UntitledEnumItemsSchema { Enum = choices } };
                else properties["choice"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Title = "Select an option", Enum = choices };
            }
            elicitation = new ElicitRequestParams
            {
                Mode = "form",
                Message = S(question, "content") ?? message,
                RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = properties, Required = choices.Count == 0 ? ["answer"] : [] }
            };
        }
        else
        {
            try
            {
                var schema = await ConfirmationSchema(e, ct);
                // Resolve tool context only for exposed tool targets, never for UI/OAuth cards.
                var context = e.Events.FirstOrDefault(raw => S(raw, "id") == id && S(raw, "type") == "tool_used");
                if (context.ValueKind == JsonValueKind.Undefined)
                {
                    var target = await e.Api.Messages(new { task_id = e.TaskId, verbose = true, start_event_id = id, order = "asc", limit = 1 }, ct);
                    context = Array(target, "messages").FirstOrDefault(raw => S(raw, "id") == id);
                }
                if (P(context, "tool_used") is { } tool) message += "\nProposed action: " + tool.GetRawText();
                elicitation = new ElicitRequestParams { Mode = "form", Message = message, RequestedSchema = ConvertSchema(schema) };
            }
            catch (NotSupportedException ex)
            {
                elicitation = new ElicitRequestParams
                {
                    Mode = "url",
                    Message = message + "\n" + ex.Message,
                    Url = S(P(e.Detail, "task") ?? Empty, "task_url") ?? "https://manus.im/app/" + Uri.EscapeDataString(e.TaskId!),
                    ElicitationId = FormId(e)
                };
            }
        }
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = FormId(e),
            ToolName = "ai_input_required",
            Title = "Input required",
            Input = InputRequest.ForElicitation(elicitation),
            State = "input-available",
            ProviderExecuted = false,
            Metadata = Meta(e)
        };
    }

    private static async Task<bool> SubmitAnswer(Execution e, AIToolCallContentPart answer, CancellationToken ct)
    {
        if (e.Status != "waiting" || answer.ToolCallId != FormId(e))
            throw new InvalidOperationException("Manus elicitation is stale or belongs to another task.");
        var result = Unwrap(answer.Output);
        var action = S(result, "action");
        if (action is "cancel" or "decline") return false; // No invented cancellation input and no task stop.
        if (action != "accept" || P(result, "content") is not { ValueKind: JsonValueKind.Object } content)
            throw new ArgumentException("Manus requires an accepted standard elicitation result with object content.");
        if (IsQuestion(S(e.Waiting, "waiting_for_event_type")))
        {
            var question = P(e.Question, "assistant_message") ?? Empty;
            var expectation = P(question, "question_expectation") ?? Empty;
            if (S(expectation, "response_method") is { } method && method != "send_message") throw new NotSupportedException("Unknown question response method.");
            var options = Array(expectation, "options").Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal);
            var answers = new List<string>();
            foreach (var p in content.EnumerateObject())
            {
                if (p.Name == "answer" && p.Value.ValueKind == JsonValueKind.String)
                { if (!string.IsNullOrWhiteSpace(p.Value.GetString())) answers.Add(p.Value.GetString()!); }
                else if (p.Name == "choice" && S(expectation, "selection_mode") != "multiple" && p.Value.ValueKind == JsonValueKind.String && options.Contains(p.Value.GetString())) answers.Add(p.Value.GetString()!);
                else if (p.Name == "choices" && S(expectation, "selection_mode") == "multiple" && p.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in p.Value.EnumerateArray())
                        if (choice.ValueKind == JsonValueKind.String && options.Contains(choice.GetString())) answers.Add(choice.GetString()!);
                        else throw new ArgumentException("Invalid Manus question choice.");
                }
                else throw new ArgumentException("Invalid Manus question answer field.");
            }
            var text = string.Join("; ", answers.Distinct());
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Manus question answer cannot be empty.");
            var body = FollowUpBody(e);
            var message = e.Body["message"] is System.Text.Json.Nodes.JsonObject raw ? (System.Text.Json.Nodes.JsonObject)raw.DeepClone() : new();
            message["content"] = text; body["message"] = message;
            e.Created = await e.Api.SendMessage(body, ct);
            return true;
        }
        var schema = await ConfirmationSchema(e, ct);
        if (!FlatSchema(schema))
        {
            if (content.EnumerateObject().Count() != 1 || S(content, "input_json") is not { } json) throw new ArgumentException("This confirmation requires input_json.");
            using var doc = JsonDocument.Parse(json); content = doc.RootElement.Clone();
        }
        if (content.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(content.GetRawText()) > 120 * 1024)
            throw new ArgumentException("Invalid Manus confirmation input size or type.");
        // Disallow schema-driven network access, including remote references nested in definitions.
        void CheckRefs(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject())
                { if (p.Name == "$ref" && (p.Value.ValueKind != JsonValueKind.String || !p.Value.GetString()!.StartsWith('#'))) throw new NotSupportedException("External confirmation schema references are not supported."); CheckRefs(p.Value); }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckRefs(child);
        }
        CheckRefs(schema);
        var validator = await JsonSchema.FromJsonAsync(schema.GetRawText(), cancellationToken: ct);
        if (validator.Validate(content.GetRawText()).Count > 0) throw new ArgumentException("Manus confirmation input does not match the pending schema.");
        e.Created = await e.Api.ConfirmAction(new { task_id = e.TaskId, event_id = S(e.Waiting, "waiting_for_event_id"), input = content }, ct);
        return B(e.Created, "confirmed") == true;
    }
}
