using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const string BrowserOriginTool = "computer_use_browser_origin_access";
    private const string AuthenticationTool = "ai_input_required";
    private const string AuthenticationOptionsPrefix = "openai-browser-auth-options-";
    private const string AuthenticationFieldsPrefix = "openai-browser-auth-fields-";

    private sealed record ComputerFollowUp(List<object> Events, AIStreamEvent? InternalForm = null);

    private static bool IsOpenAiComputerUseInput(AIToolCallContentPart tool)
        => tool.ToolCallId is not null
           && (tool.ToolCallId.StartsWith(AuthenticationOptionsPrefix, StringComparison.Ordinal)
               || tool.ToolCallId.StartsWith(AuthenticationFieldsPrefix, StringComparison.Ordinal)
               || tool.ToolCallId.StartsWith("openai-browser-origin-", StringComparison.Ordinal));

    private IEnumerable<AIStreamEvent> MapOpenAiComputerUseAction(
        JsonElement action, OpenAiAgentStreamState state, DateTimeOffset timestamp)
    {
        var requestId = TryGetOpenAiString(action, "request_id");
        if (string.IsNullOrWhiteSpace(requestId)
            || !TryGetOpenAiProperty(action, "request", out var request))
            yield break;

        var kind = TryGetOpenAiString(request, "type");
        if (kind == "browser_origin_access")
        {
            var id = $"openai-browser-origin-{requestId}";
            if (!state.EmittedApprovalIds.Add(id))
                yield break;
            yield return CreateOpenAiAgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = BrowserOriginTool,
                Title = TryGetOpenAiString(request, "reason") ?? "Allow browser access?",
                Input = new { origin = TryGetOpenAiString(request, "origin"), reason = TryGetOpenAiString(request, "reason") },
                ProviderExecuted = true
            }, timestamp, null);
            yield return CreateOpenAiAgentEvent("tool-approval-request", id,
                new AIToolApprovalRequestEventData { ApprovalId = requestId, ToolCallId = id }, timestamp, null);
        }
        else if (kind == "browser_authentication")
        {
            var id = AuthenticationOptionsPrefix + requestId;
            if (state.EmittedApprovalIds.Add(id))
                yield return CreateAuthenticationForm(action, null, timestamp);
        }
    }

    private AIStreamEvent CreateAuthenticationForm(JsonElement action, string? optionId, DateTimeOffset timestamp)
    {
        var requestId = TryGetOpenAiString(action, "request_id")!;
        var request = action.GetProperty("request");
        var options = GetArray(request, "options");
        var fields = GetArray(request, "fields");
        var properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal);
        var required = new List<string>();
        string id;
        if (optionId is null && options.Count > 0)
        {
            id = AuthenticationOptionsPrefix + requestId;
            properties["selected_option"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = "Sign-in method",
                OneOf = options.Select(option => new ElicitRequestParams.EnumSchemaOption
                {
                    Const = TryGetOpenAiString(option, "id") ?? string.Empty,
                    Title = TryGetOpenAiString(option, "label") ?? string.Empty
                }).ToList()
            };
            required.Add("selected_option");
        }
        else
        {
            id = optionId is null
                ? AuthenticationFieldsPrefix + requestId
                : AuthenticationFieldsPrefix + requestId + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(optionId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var allowedIds = optionId is null ? null : GetArray(options.Single(option => TryGetOpenAiString(option, "id") == optionId), "field_ids")
                .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            foreach (var field in fields.Where(field => allowedIds is null || allowedIds.Contains(TryGetOpenAiString(field, "id"))))
            {
                var fieldId = TryGetOpenAiString(field, "id")!;
                properties.Add(fieldId, new ElicitRequestParams.StringSchema
                {
                    Title = TryGetOpenAiString(field, "label") ?? fieldId,
                    Format = TryGetOpenAiString(field, "type") == "email" ? "email" : null,
                    MaxLength = 16384
                });
                if (TryGetOpenAiBool(field, "required") == true)
                    required.Add(fieldId);
            }
        }

        var origin = TryGetOpenAiString(request, "credential_origin") ?? "Origin not supplied";
        var reason = TryGetOpenAiString(request, "reason") ?? "Sign in to continue";
        var elicitation = new ElicitRequestParams
        {
            Mode = "form",
            Message = $"{reason}\nCredential origin: {origin}. Verify this destination before entering any values.",
            RequestedSchema = new ElicitRequestParams.RequestSchema { Properties = properties, Required = required }
        };
        return CreateOpenAiAgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = AuthenticationTool, Title = "Sign in", Input = InputRequest.ForElicitation(elicitation),
            ProviderExecuted = false
        }, timestamp, null);
    }

    private ComputerFollowUp BuildOpenAiComputerUseFollowUpEvents(
        AIRequest request, JsonElement session)
    {
        var actions = GetArray(session, "required_actions");
        var results = new List<object>();
        var answered = (request.Input?.Items ?? [])
            .SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>()
            .Where(tool => IsOpenAiComputerUseInput(tool)
                           && (tool.Output is not null || tool.Approval?.Approved is not null))
            .ToList();
        // A UI transcript includes earlier answered forms. Only the latest answer represents
        // the current interaction; never replay a previous approval or method selection.
        if (answered.Count > 1)
            answered = [answered[^1]];
        foreach (var tool in answered)
        {
            var id = tool.ToolCallId;
            var isOrigin = id.StartsWith("openai-browser-origin-", StringComparison.Ordinal);
            var isOptions = id.StartsWith(AuthenticationOptionsPrefix, StringComparison.Ordinal);
            var isFields = id.StartsWith(AuthenticationFieldsPrefix, StringComparison.Ordinal);
            var tail = isOrigin ? id["openai-browser-origin-".Length..]
                : isOptions ? id[AuthenticationOptionsPrefix.Length..]
                : id[AuthenticationFieldsPrefix.Length..];
            var separator = isFields ? tail.IndexOf(':') : -1;
            var requestId = separator < 0 ? tail : tail[..separator];
            var action = actions.FirstOrDefault(value => TryGetOpenAiString(value, "type") == "computer_use_approval_request"
                && TryGetOpenAiString(value, "request_id") == requestId);
            if (action.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Browser approval is no longer pending. Refresh the session before responding.");
            var challenge = action.GetProperty("request");
            if (isOrigin)
            {
                if (TryGetOpenAiString(challenge, "type") != "browser_origin_access" || tool.Approval?.Approved is not bool approved)
                    throw new InvalidOperationException("Invalid browser origin approval.");
                results.Add(ComputerResult(requestId, new { type = "browser_origin_access", decision = approved ? "approve" : "deny" }));
                continue;
            }
            if (TryGetOpenAiString(challenge, "type") != "browser_authentication")
                throw new InvalidOperationException("Invalid browser authentication request.");
            var value = JsonSerializer.SerializeToElement(tool.Output, JsonSerializerOptions.Web);
            if (TryGetOpenAiProperty(value, "structuredContent", out var structured))
                value = structured;
            if (TryGetOpenAiString(value, "action") == "cancel" || TryGetOpenAiString(value, "action") == "decline")
            {
                results.Add(ComputerResult(requestId, new { type = "browser_authentication", action = "cancel" }));
                continue;
            }
            if (TryGetOpenAiString(value, "action") != "accept"
                || !TryGetOpenAiProperty(value, "content", out var content) || content.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("A browser sign-in form requires an accepted elicitation response.");

            var options = GetArray(challenge, "options");
            string? optionId = null;
            if (isOptions)
            {
                optionId = TryGetOpenAiString(content, "selected_option");
                if (content.EnumerateObject().Count() != 1)
                    throw new InvalidOperationException("The sign-in method form must contain only the selected option.");
                var option = options.FirstOrDefault(item => TryGetOpenAiString(item, "id") == optionId);
                if (option.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Invalid sign-in method.");
                if (GetArray(option, "field_ids").Count > 0)
                {
                    return new ComputerFollowUp([], CreateAuthenticationForm(action, optionId, DateTimeOffset.UtcNow));
                }
            }
            else if (separator >= 0)
            {
                try
                {
                    var encoded = tail[(separator + 1)..].Replace('-', '+').Replace('_', '/');
                    optionId = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("Invalid sign-in method identifier.");
                }
            }
            else if (options.Count > 0)
                throw new InvalidOperationException("Select a sign-in method first.");

            var selected = optionId is null ? default : options.FirstOrDefault(item => TryGetOpenAiString(item, "id") == optionId);
            if (optionId is not null && selected.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Sign-in method is no longer available.");
            var permitted = optionId is null
                ? GetArray(challenge, "fields").Select(item => TryGetOpenAiString(item, "id")).ToHashSet(StringComparer.Ordinal)
                : GetArray(selected, "field_ids").Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            var fields = new List<object>();
            foreach (var property in isOptions ? Enumerable.Empty<JsonProperty>() : content.EnumerateObject())
            {
                if (!permitted.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Invalid sign-in field.");
                var fieldValue = property.Value.GetString()!;
                if (fieldValue.Length > 16384)
                    throw new InvalidOperationException("Sign-in field exceeds the allowed length.");
                if (fieldValue.Length > 0)
                    fields.Add(new { field_id = property.Name, value = fieldValue });
            }
            if (fields.Count > 6 || (optionId is null && permitted.Count > 0 && fields.Count == 0))
                throw new InvalidOperationException("Invalid number of sign-in fields.");
            foreach (var field in GetArray(challenge, "fields").Where(item => permitted.Contains(TryGetOpenAiString(item, "id"))))
                if (TryGetOpenAiBool(field, "required") == true
                    && (!content.TryGetProperty(TryGetOpenAiString(field, "id")!, out var fieldValue)
                        || fieldValue.ValueKind != JsonValueKind.String
                        || fieldValue.GetString() is not { Length: > 0 }))
                    throw new InvalidOperationException("A required sign-in field is missing.");

            var response = new Dictionary<string, object?> { ["type"] = "browser_authentication", ["action"] = "submit", ["fields"] = fields };
            if (optionId is not null)
                response["selected_option"] = optionId;
            if (JsonSerializer.SerializeToUtf8Bytes(response, JsonSerializerOptions.Web).Length > 120 * 1024)
                throw new InvalidOperationException("Sign-in submission exceeds the allowed size.");
            results.Add(ComputerResult(requestId, response));
        }

        return new ComputerFollowUp(results);
    }

    private static object ComputerResult(string requestId, object response) => new
    {
        type = "agent.session.input.computer_use_approval_request_result", request_id = requestId, response
    };

    private static List<JsonElement> GetArray(JsonElement parent, string name)
        => TryGetOpenAiProperty(parent, name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.Clone()).ToList() : [];
}
