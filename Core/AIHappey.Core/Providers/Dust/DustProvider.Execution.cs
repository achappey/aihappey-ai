using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    private sealed record Approval(string MessageId, string ActionId, string Decision, string? Cursor,
        string? ConversationId = null, string? StreamMessageId = null);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var identity = FindIdentity(request, route);
        var options = Options(request);
        var nativeApprovals = ReadApprovals(options);
        var (text, approvals) = nativeApprovals.Count > 0 ? (null, new List<AIToolCallContentPart>()) : LatestInput(request);
        if (nativeApprovals.Count == 0)
            nativeApprovals = ReadToolApprovals(approvals);
        var conversationId = Str(identity, "conversationId");
        var created = conversationId is null;
        if (nativeApprovals.Count > 0 && conversationId is null)
            throw new ArgumentException("Dust approval responses require a conversation identity.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var messages = new List<JsonObject>();
        var cursors = new Dictionary<string, string?>();
        if (nativeApprovals.Count > 0)
        {
            for (var index = 0; index < nativeApprovals.Count; index++)
            {
                var approval = nativeApprovals[index];
                Segment(approval.MessageId);
                Segment(approval.ActionId);
                var targetConversation = approval.ConversationId ?? conversationId!;
                Segment(targetConversation);
                var streamMessageId = approval.StreamMessageId ?? Str(identity, "messageId")
                    ?? (targetConversation == conversationId ? approval.MessageId : null)
                    ?? throw new ArgumentException("Dust child-agent approval requires the parent streamMessageId.");
                Segment(streamMessageId);
                nativeApprovals[index] = approval with { StreamMessageId = streamMessageId };
                await SendJsonAsync(HttpMethod.Post,
                    $"{route.Path}/assistant/conversations/{Segment(targetConversation)}/messages/{Segment(approval.MessageId)}/validate-action",
                    new { actionId = approval.ActionId, approved = approval.Decision }, ct);
                if (messages.All(message => Str(message, "sId") != streamMessageId))
                    messages.Add(new JsonObject { ["sId"] = streamMessageId });
                cursors[streamMessageId] = approval.Cursor ?? Str(identity, "lastEventId") ?? Str(options, "lastEventId");
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
                var latestUserId = Str(acknowledgement["message"], "sId")
                    ?? throw new InvalidOperationException("Dust did not return the newly created user message sId.");
                if (conversation["content"] is JsonArray rows)
                    foreach (var row in rows.OfType<JsonArray>())
                        messages.AddRange(row.OfType<JsonObject>().Where(message =>
                            Str(message, "type") == "agent_message"
                            && Str(message, "parentMessageId") == latestUserId
                            && Str(message["configuration"], "sId") == route.AgentId));
            }
            else
            {
                acknowledgement = await SendJsonAsync(HttpMethod.Post,
                    $"{route.Path}/assistant/conversations/{Segment(conversationId)}/messages", message, ct);
                if (acknowledgement["agentMessages"] is JsonArray agents)
                    messages.AddRange(agents.OfType<JsonObject>());
            }
        }

        if (created)
            foreach (var receipt in IdentityEvents(route, conversationId!))
                yield return receipt;
        var selected = messages.Where(message =>
            nativeApprovals.Count > 0 || Str(message["configuration"], "sId") == route.AgentId).ToList();
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

    private static List<Approval> ReadApprovals(JsonObject options)
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
            if (Str(value, "conversationId") is { } conversation) Segment(conversation);
            if (Str(value, "streamMessageId") is { } stream) Segment(stream);
            result.Add(new Approval(message, action, decision, Str(value, "lastEventId"),
                Str(value, "conversationId"), Str(value, "streamMessageId")));
        }
        return result;
    }

    private static List<Approval> ReadToolApprovals(List<AIToolCallContentPart> tools)
    {
        var result = new List<Approval>();
        foreach (var tool in tools)
        {
            if (tool.ProviderExecuted != true)
                throw new ArgumentException("Dust can only resume approval responses for provider-executed tools.");
            var parts = tool.Approval?.Id?.Split(':');
            if (parts is null || parts.Length is not (2 or 3))
                throw new ArgumentException("Dust approval IDs must contain 'conversationId:messageId:actionId'.");
            foreach (var segment in parts) Segment(segment);
            var metadata = DustMetadata(Node(tool.Metadata));
            result.Add(new Approval(parts[^2], parts[^1], tool.Approval!.Approved == true ? "approved" : "rejected",
                Str(metadata, "lastEventId"), parts.Length == 3 ? parts[0] : null,
                Str(metadata, "streamMessageId") ?? Str(metadata, "messageId")));
        }
        return result;
    }
}
