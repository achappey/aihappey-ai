using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private sealed record Approval(string MessageId, string ActionId, string Decision, string? Cursor);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var identity = FindIdentity(request, route);
        var options = Options(request);
        var nativeApprovals = ReadApprovals(request, options);
        var (text, approvals) = nativeApprovals.Count > 0 ? (null, new List<AIToolCallContentPart>()) : LatestInput(request);
        if (nativeApprovals.Count == 0)
            nativeApprovals = ReadToolApprovals(approvals);
        var conversationId = Str(identity, "conversationId");
        if (nativeApprovals.Count > 0 && conversationId is null)
            throw new ArgumentException("Dust approval responses require a conversation identity.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var messages = new List<JsonObject>();
        var cursors = new Dictionary<string, string?>();
        if (nativeApprovals.Count > 0)
        {
            foreach (var approval in nativeApprovals)
            {
                Segment(approval.MessageId);
                Segment(approval.ActionId);
                await SendJsonAsync(HttpMethod.Post,
                    $"{route.Path}/assistant/conversations/{Segment(conversationId!)}/messages/{Segment(approval.MessageId)}/validate-action",
                    new { actionId = approval.ActionId, approved = approval.Decision }, ct);
                if (messages.All(message => Str(message, "sId") != approval.MessageId))
                    messages.Add(new JsonObject { ["sId"] = approval.MessageId });
                cursors[approval.MessageId] = approval.Cursor ?? Str(identity, "lastEventId") ?? Str(options, "lastEventId");
            }
        }
        else
        {
            var message = BuildMessage(route, text!, options);
            JsonNode acknowledgement;
            if (conversationId is null)
            {
                var payload = new JsonObject
                {
                    ["message"] = message, ["blocking"] = false, ["skipToolsValidation"] = false
                };
                foreach (var field in new[] { "title", "visibility", "spaceId" })
                    if (options[field] is { } value) payload[field] = value.DeepClone();
                if (Str(payload, "visibility") is { } visibility && visibility is not ("unlisted" or "workspace"))
                    throw new ArgumentException("Dust visibility must be 'unlisted' or 'workspace'.");
                if (Str(payload, "spaceId") is { } space) Segment(space);
                acknowledgement = await SendJsonAsync(HttpMethod.Post,
                    $"{route.Path}/assistant/conversations", payload, ct);
                var conversation = acknowledgement["conversation"] as JsonObject
                    ?? throw new InvalidOperationException("Dust did not return a conversation.");
                conversationId = Str(conversation, "sId")
                    ?? throw new InvalidOperationException("Dust did not return a conversation sId.");
                Segment(conversationId);
                var latestUserId = Str(acknowledgement["message"], "sId");
                if (conversation["content"] is JsonArray rows)
                    foreach (var row in rows.OfType<JsonArray>())
                        messages.AddRange(row.OfType<JsonObject>().Where(message =>
                            Str(message, "type") == "agent_message"
                            && (latestUserId is null || Str(message, "parentMessageId") == latestUserId)));
            }
            else
            {
                acknowledgement = await SendJsonAsync(HttpMethod.Post,
                    $"{route.Path}/assistant/conversations/{Segment(conversationId)}/messages", message, ct);
                if (acknowledgement["agentMessages"] is JsonArray agents)
                    messages.AddRange(agents.OfType<JsonObject>());
            }
        }

        foreach (var receipt in IdentityEvents(route, conversationId!))
            yield return receipt;
        var selected = messages.Where(message =>
            Str(message["configuration"], "sId") is not { } agent || agent == route.AgentId).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Dust did not create a message for the selected agent.");
        foreach (var message in selected)
        {
            var messageId = Str(message, "sId") ?? throw new InvalidOperationException("Dust agent message has no sId.");
            Segment(messageId);
            var cursor = cursors.GetValueOrDefault(messageId);
            await foreach (var evt in StreamMessageAsync(route, conversationId!, messageId, cursor, nativeApprovals, ct))
                yield return evt;
        }
    }

    private static JsonObject BuildMessage(Route route, string text, JsonObject options)
    {
        var configured = options["context"] as JsonObject;
        var context = new JsonObject
        {
            ["username"] = Str(configured, "username") ?? "AIHappey",
            ["timezone"] = Str(configured, "timezone") ?? "UTC"
        };
        foreach (var field in new[] { "fullName", "email", "profilePictureUrl" })
            if (configured?[field] is { } value) context[field] = value.DeepClone();
        foreach (var field in new[] { "username", "timezone", "fullName", "email" })
            if (Str(context, field) is { } value && (value.Length > 254 || field == "username" && string.IsNullOrWhiteSpace(value)))
                throw new ArgumentException($"Invalid Dust context.{field}.");
        return new JsonObject
        {
            ["content"] = text, ["mentions"] = new JsonArray(new JsonObject { ["configurationId"] = route.AgentId }),
            ["context"] = context, ["blocking"] = false, ["skipToolsValidation"] = false
        };
    }

    private static List<Approval> ReadApprovals(AIRequest request, JsonObject options)
    {
        var result = new List<Approval>();
        var configured = options["approvals"] as JsonArray;
        var values = configured?.OfType<JsonObject>().ToList() ?? [];
        if (options["approval"] is JsonObject single) values.Add(single);
        foreach (var value in values)
        {
            var message = Str(value, "messageId") ?? throw new ArgumentException("Dust approval requires messageId.");
            var action = Str(value, "actionId") ?? throw new ArgumentException("Dust approval requires actionId.");
            var decision = Str(value, "approved");
            if (decision is not ("approved" or "rejected"))
                throw new ArgumentException("Dust approval.approved must be 'approved' or 'rejected'.");
            Segment(message);
            Segment(action);
            result.Add(new Approval(message, action, decision, Str(value, "lastEventId")));
        }
        return result;
    }

    private static List<Approval> ReadToolApprovals(List<AIToolCallContentPart> tools)
    {
        var result = new List<Approval>();
        foreach (var tool in tools)
        {
            var parts = tool.Approval?.Id?.Split(':');
            if (parts is not { Length: 2 })
                throw new ArgumentException("Dust approval IDs must contain 'messageId:actionId'.");
            var metadata = Node(tool.Metadata);
            if (metadata is JsonObject outer && outer["dust"] is JsonObject dust)
                metadata = dust["raw"] ?? dust;
            result.Add(new Approval(parts[0], parts[1], tool.Approval!.Approved == true ? "approved" : "rejected",
                Str(metadata, "lastEventId")));
        }
        return result;
    }
}
