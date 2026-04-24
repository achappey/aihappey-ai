using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Microsoft;

public partial class MicrosoftProvider
{
    private const string CopilotModelId = "microsoft/copilot";
    private const string CopilotConversationToolName = "create_conversation";
    private const string CopilotConversationEndpoint = "beta/copilot/conversations";

    private static readonly string[] CopilotGraphScopes =
    [
        "Sites.Read.All",
        "Mail.Read",
        "People.Read.All",
        "OnlineMeetingTranscript.Read.All",
        "Chat.Read",
        "ChannelMessage.Read.All",
        "ExternalItem.Read.All"
    ];

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ApplyDelegatedGraphAuthHeaderAsync(cancellationToken);

        var conversation = await ResolveConversationAsync(request, cancellationToken);
        var payload = BuildCopilotChatPayload(request);
        using var response = await _client.PostAsJsonAsync(
            $"{CopilotConversationEndpoint}/{Uri.EscapeDataString(conversation.Id)}/chat",
            payload,
            Json,
            cancellationToken);

        var responseJson = await ReadJsonElementAsync(response, "Microsoft Copilot chat", cancellationToken);
        return CreateUnifiedResponse(request, responseJson, conversation.Created ? conversation : null);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await ApplyDelegatedGraphAuthHeaderAsync(cancellationToken);

        var timestamp = DateTimeOffset.UtcNow;
        var conversation = await ResolveConversationAsync(request, cancellationToken);

        if (conversation.Created)
        {
            foreach (var evt in CreateConversationToolEvents(conversation, timestamp))
                yield return evt;
        }

        var payload = BuildCopilotChatPayload(request);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{CopilotConversationEndpoint}/{Uri.EscapeDataString(conversation.Id)}/chatOverStream")
        {
            Content = JsonContent.Create(payload, options: Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Microsoft Copilot chatOverStream API error: {err}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var textStarted = false;
        var lastText = string.Empty;
        JsonElement? lastConversation = null;

        await foreach (var sseData in ReadSseDataAsync(reader, cancellationToken))
        {
            if (!TryParseJson(sseData, out var chunk))
                continue;

            lastConversation = chunk.Clone();
            var assistantText = ExtractAssistantText(chunk);
            var delta = ComputeTextDelta(lastText, assistantText);
            lastText = assistantText ?? lastText;

            if (!string.IsNullOrEmpty(delta))
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateStreamEvent(
                        "text-start",
                        conversation.Id,
                        new AITextStartEventData { ProviderMetadata = CreateLooseProviderMetadata(chunk) },
                        timestamp,
                        CreateResponseMetadata(chunk, request.Model));
                }

                yield return CreateStreamEvent(
                    "text-delta",
                    conversation.Id,
                    new AITextDeltaEventData
                    {
                        Delta = delta,
                        ProviderMetadata = CreateLooseProviderMetadata(chunk)
                    },
                    DateTimeOffset.UtcNow,
                    CreateResponseMetadata(chunk, request.Model));
            }
        }

        var finalConversation = lastConversation;
        if (textStarted)
        {
            yield return CreateStreamEvent(
                "text-end",
                conversation.Id,
                new AITextEndEventData { ProviderMetadata = finalConversation.HasValue ? CreateLooseProviderMetadata(finalConversation.Value) : null },
                DateTimeOffset.UtcNow,
                finalConversation.HasValue ? CreateResponseMetadata(finalConversation.Value, request.Model) : null);
        }

        if (finalConversation.HasValue)
        {
            foreach (var source in CreateSourceEvents(finalConversation.Value, DateTimeOffset.UtcNow))
                yield return source;
        }

        yield return CreateFinishEvent(
            conversation.Id,
            finalConversation,
            DateTimeOffset.UtcNow);
    }

    private async Task<CopilotConversationResolution> ResolveConversationAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        if (TryFindConversationId(request, out var existingConversationId))
            return new CopilotConversationResolution(existingConversationId, false, null);

        using var response = await _client.PostAsJsonAsync(
            CopilotConversationEndpoint,
            new Dictionary<string, object?>(),
            Json,
            cancellationToken);

        var conversation = await ReadJsonElementAsync(response, "Microsoft Copilot create conversation", cancellationToken);
        var id = ExtractString(conversation, "id")
                 ?? throw new InvalidOperationException("Microsoft Copilot create conversation response did not include an id.");

        return new CopilotConversationResolution(id, true, conversation.Clone());
    }

    private static Dictionary<string, object?> BuildCopilotChatPayload(AIRequest request)
    {
        var text = ExtractLatestUserText(request)
                   ?? request.Input?.Text
                   ?? request.Instructions
                   ?? throw new InvalidOperationException("Microsoft Copilot requires a user message.");

        var payload = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?>
            {
                ["text"] = text
            },
            ["locationHint"] = new Dictionary<string, object?>
            {
                ["timeZone"] = TimeZoneInfo.Local.Id
            }
        };

        var providerMetadata = GetMicrosoftProviderMetadata(request.Metadata);
        if (providerMetadata is not null)
        {
            foreach (var prop in providerMetadata.Value.EnumerateObject())
            {
                if (prop.NameEquals("conversationId") || prop.NameEquals("conversation_id"))
                    continue;

                payload[prop.Name] = prop.Value.Clone();
            }
        }

        return payload;
    }

    private AIResponse CreateUnifiedResponse(
        AIRequest request,
        JsonElement response,
        CopilotConversationResolution? createdConversation)
    {
        var content = new List<AIContentPart>();

        if (createdConversation?.RawConversation is JsonElement rawConversation)
            content.Add(CreateConversationToolPart(createdConversation.Id, rawConversation));

        var assistantText = ExtractAssistantText(response);
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = assistantText,
                Metadata = new Dictionary<string, object?>
                {
                    ["microsoft.raw"] = response.Clone()
                }
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model ?? CopilotModelId,
            Status = ExtractString(response, "state") ?? "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = content,
                        Metadata = CreateResponseMetadata(response, request.Model)
                    },
                    .. CreateSourceOutputItems(response)
                ],
                Metadata = CreateResponseMetadata(response, request.Model)
            },
            Usage = CreateUsage(response),
            Metadata = CreateResponseMetadata(response, request.Model)
        };
    }

    private AIToolCallContentPart CreateConversationToolPart(string conversationId, JsonElement rawConversation)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildConversationToolCallId(conversationId),
            ToolName = CopilotConversationToolName,
            Title = "Create Copilot conversation",
            Input = JsonSerializer.SerializeToElement(new { }, Json),
            Output = CreateConversationToolResult(rawConversation),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = new Dictionary<string, object?>
            {
                ["type"] = CopilotConversationToolName,
                ["conversationId"] = conversationId,
                ["tool_name"] = CopilotConversationToolName
            }
        };

    private IEnumerable<AIStreamEvent> CreateConversationToolEvents(
        CopilotConversationResolution conversation,
        DateTimeOffset timestamp)
    {
        if (conversation.RawConversation is not JsonElement rawConversation)
            yield break;

        var toolCallId = BuildConversationToolCallId(conversation.Id);
        var providerMetadata = CreateProviderScopedMetadata(new Dictionary<string, object>
        {
            ["type"] = CopilotConversationToolName,
            ["conversationId"] = conversation.Id,
            ["tool_name"] = CopilotConversationToolName
        });

        yield return CreateStreamEvent(
            "tool-input-available",
            toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = CopilotConversationToolName,
                Title = "Create Copilot conversation",
                Input = JsonSerializer.SerializeToElement(new { }, Json),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateStreamEvent(
            "tool-output-available",
            toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = CopilotConversationToolName,
                ProviderExecuted = true,
                Output = CreateConversationToolResult(rawConversation),
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private static CallToolResult CreateConversationToolResult(JsonElement rawConversation)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                conversationId = ExtractString(rawConversation, "id"),
                conversation = rawConversation.Clone()
            }, Json)
        };

    private static bool TryFindConversationId(AIRequest request, out string conversationId)
    {
        conversationId = request.Metadata.GetProviderOption<string>("microsoft", "conversationId")
                         ?? request.Metadata.GetProviderOption<string>("microsoft", "conversation_id")
                         ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(conversationId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (toolPart.ProviderExecuted != true)
                    continue;

                if (!string.Equals(toolPart.ToolName, CopilotConversationToolName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(toolPart.Title, CopilotConversationToolName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryExtractConversationId(toolPart.Output, out conversationId))
                    return true;

                if (TryExtractConversationId(toolPart.Metadata, out conversationId))
                    return true;
            }
        }

        conversationId = string.Empty;
        return false;
    }

    private static bool TryExtractConversationId(object? value, out string conversationId)
    {
        conversationId = string.Empty;
        if (value is null)
            return false;

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, Json);

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty("structuredContent", out var structuredContent))
            element = structuredContent;

        if (element.TryGetProperty("conversationId", out var id) && id.ValueKind == JsonValueKind.String)
            conversationId = id.GetString() ?? string.Empty;
        else if (element.TryGetProperty("conversation_id", out var snakeId) && snakeId.ValueKind == JsonValueKind.String)
            conversationId = snakeId.GetString() ?? string.Empty;
        else if (element.TryGetProperty("conversation", out var conversation) && conversation.ValueKind == JsonValueKind.Object)
            conversationId = ExtractString(conversation, "id") ?? string.Empty;
        else if (element.TryGetProperty("microsoft", out var microsoft) && microsoft.ValueKind == JsonValueKind.Object)
            conversationId = ExtractString(microsoft, "conversationId") ?? ExtractString(microsoft, "conversation_id") ?? string.Empty;

        return !string.IsNullOrWhiteSpace(conversationId);
    }

    private static string? ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string? ExtractAssistantText(JsonElement conversation)
    {
        if (!conversation.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var message in messages.EnumerateArray().Reverse())
        {
            var text = ExtractString(message, "text");
            if (!string.IsNullOrWhiteSpace(text) && !LooksLikeUserEcho(message, messages))
                return text;
        }

        foreach (var message in messages.EnumerateArray().Reverse())
        {
            var text = ExtractString(message, "text");
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static bool LooksLikeUserEcho(JsonElement candidate, JsonElement messages)
    {
        var candidateText = ExtractString(candidate, "text");
        var firstText = messages.EnumerateArray()
            .Select(message => ExtractString(message, "text"))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return !string.IsNullOrWhiteSpace(candidateText)
               && string.Equals(candidateText, firstText, StringComparison.Ordinal);
    }

    private static string ComputeTextDelta(string previousText, string? currentText)
    {
        if (string.IsNullOrEmpty(currentText) || string.Equals(previousText, currentText, StringComparison.Ordinal))
            return string.Empty;

        return currentText.StartsWith(previousText, StringComparison.Ordinal)
            ? currentText[previousText.Length..]
            : currentText;
    }

    private static Dictionary<string, object?> CreateResponseMetadata(JsonElement response, string? model)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.raw"] = response.Clone(),
            ["microsoft.conversationId"] = ExtractString(response, "id"),
            ["microsoft.state"] = ExtractString(response, "state"),
            ["microsoft.turnCount"] = ExtractInt(response, "turnCount"),
            ["model"] = model ?? CopilotModelId
        };

    private static object CreateUsage(JsonElement response)
        => new Dictionary<string, object?>
        {
            ["turn_count"] = ExtractInt(response, "turnCount")
        };

    private IEnumerable<AIOutputItem> CreateSourceOutputItems(JsonElement response)
    {
        foreach (var attribution in EnumerateAttributions(response))
        {
            var url = ExtractString(attribution, "seeMoreWebUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            yield return new AIOutputItem
            {
                Type = "source-url",
                Role = "assistant",
                Metadata = new Dictionary<string, object?>
                {
                    ["source.url"] = url,
                    ["source.title"] = ExtractString(attribution, "providerDisplayName") ?? url,
                    ["microsoft.attribution.raw"] = attribution.Clone()
                }
            };
        }
    }

    private IEnumerable<AIStreamEvent> CreateSourceEvents(JsonElement response, DateTimeOffset timestamp)
    {
        var index = 0;
        foreach (var attribution in EnumerateAttributions(response))
        {
            var url = ExtractString(attribution, "seeMoreWebUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            yield return CreateStreamEvent(
                "source-url",
                $"microsoft-source-{index++}",
                new AISourceUrlEventData
                {
                    SourceId = $"microsoft-source-{index}",
                    Url = url,
                    Title = ExtractString(attribution, "providerDisplayName") ?? url,
                    ProviderMetadata = CreateProviderScopedMetadata(new Dictionary<string, object>
                    {
                        ["attribution"] = attribution.Clone()
                    })
                },
                timestamp,
                CreateResponseMetadata(response, CopilotModelId));
        }
    }

    private static IEnumerable<JsonElement> EnumerateAttributions(JsonElement response)
    {
        if (!response.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("attributions", out var attributions) || attributions.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var attribution in attributions.EnumerateArray())
                yield return attribution.Clone();
        }
    }

    private AIStreamEvent CreateFinishEvent(
        string id,
        JsonElement? response,
        DateTimeOffset timestamp)
    {
        var rawUsage = response.HasValue ? CreateUsage(response.Value) : null;
        return CreateStreamEvent(
            "finish",
            id,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = CopilotModelId,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    CopilotModelId,
                    timestamp,
                    rawUsage,
                    additionalProperties: response.HasValue
                        ? new Dictionary<string, object?> { [GetIdentifier()] = new { raw = response.Value.Clone() } }
                        : null)
            },
            timestamp,
            response.HasValue ? CreateResponseMetadata(response.Value, CopilotModelId) : null);
    }

    private static JsonElement? GetMicrosoftProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("microsoft", out var value) || value is null)
            return null;

        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
        return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
    }

    private static async Task<JsonElement> ReadJsonElementAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"{operation} API error: {raw}");

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"{operation} returned an empty response.");

        return JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone();
    }

    private static async IAsyncEnumerable<string> ReadSseDataAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append(line[5..].TrimStart());
        }

        if (builder.Length > 0)
            yield return builder.ToString();
    }

    private static bool TryParseJson(string raw, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(raw, Json).Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            element = default;
            return false;
        }
    }

    private AIStreamEvent CreateStreamEvent(
        string type,
        string? eventId,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static Dictionary<string, Dictionary<string, object>> CreateProviderScopedMetadata(Dictionary<string, object> metadata)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft"] = metadata
        };

    private static Dictionary<string, object> CreateLooseProviderMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["raw"] = raw.Clone()
        };

    private static string BuildConversationToolCallId(string conversationId)
        => $"microsoft-create-conversation-{conversationId}";

    private static string? ExtractString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ExtractInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private sealed record CopilotConversationResolution(
        string Id,
        bool Created,
        JsonElement? RawConversation);
}
