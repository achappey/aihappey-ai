using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Xpectrum;

/// <summary>
/// Unified mapping. Xpectrum keeps conversation memory server-side, so only the last user message is sent and
/// continuity is carried by a provider-executed identity tool holding <c>thread_id</c>, <c>run_id</c> and <c>user</c>.
/// </summary>
public sealed partial class XpectrumProvider
{
    private const string IdentityTool = "xpectrum_thread";

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => ExecuteUnifiedAsync(request, null, cancellationToken);

    private async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, string? endUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = UnifiedChatOptions(request, endUser);
        var completion = await CompleteChatAsync(options, cancellationToken);
        var native = JsonSerializer.SerializeToElement(completion);
        var response = completion.ToUnifiedResponse(GetIdentifier());
        var identity = Identity(native, options);
        var items = response.Output?.Items ?? [];
        var tool = IdentityPart(identity);
        var assistant = items.FirstOrDefault(item => item.Role == "assistant" && item.Content is not null);
        if (assistant is not null)
            assistant.Content!.Insert(0, tool);
        else
            items.Insert(0, new AIOutputItem { Role = "assistant", Content = [tool] });
        if (native.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
            items.AddRange(citations.EnumerateArray().Select(CitationItem));
        var metadata = response.Metadata ?? [];
        metadata[GetIdentifier()] = identity;
        return new AIResponse
        {
            ProviderId = response.ProviderId, Model = response.Model, Status = response.Status, Usage = response.Usage,
            Metadata = metadata,
            Output = new AIOutput { Items = items, Metadata = response.Output?.Metadata }
        };
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => StreamUnifiedAsync(request, null, cancellationToken);

    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, string? endUser, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = UnifiedChatOptions(request, endUser);
        var identityEmitted = false;
        IEnumerable<AIStreamEvent> MapChunk(ChatCompletionUpdate update)
        {
            var chunk = JsonSerializer.SerializeToElement(update);
            if (!identityEmitted && (Str(chunk, "thread_id") ?? Str(chunk, "run_id")) is not null)
            {
                identityEmitted = true;
                var identity = Identity(chunk, options);
                var id = ToolCallId(identity);
                var metadata = new Dictionary<string, object?> { [GetIdentifier()] = identity };
                yield return StreamEvent("tool-input-available", id, new AIToolInputAvailableEventData
                {
                    ToolName = IdentityTool, Title = "Xpectrum thread", Input = IdentityInput(identity), ProviderExecuted = true
                }, metadata);
                yield return StreamEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = IdentityTool, Output = new CallToolResult { StructuredContent = identity }, ProviderExecuted = true
                }, metadata);
            }
            if (chunk.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                foreach (var citation in citations.EnumerateArray())
                    yield return StreamEvent("source-document", CitationId(citation), CitationData(citation), null);
            if (chunk.TryGetProperty("providerMetadata", out var provider) && provider.TryGetProperty(GetIdentifier(), out var scoped)
                && scoped.TryGetProperty("event", out var runEvent))
                yield return StreamEvent("data-xpectrum-run", Str(runEvent, "id"), new AIDataEventData
                {
                    Id = Str(runEvent, "id"), Data = runEvent.Clone()
                }, null);
        }
        await foreach (var item in this.StreamUnifiedViaChatCompletionsAsync(
            request, options, rawChunkMapper: MapChunk, cancellationToken: cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endUser = endUserIdResolver.Resolve(request);
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), endUser, cancellationToken))
            foreach (var part in item.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), request.Metadata?.UserId, cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), request.Metadata?.UserId, cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return item;
    }

    private ChatCompletionOptions UnifiedChatOptions(AIRequest request, string? endUser)
    {
        var raw = request.Metadata?.TryGetValue(GetIdentifier(), out var scoped) == true && scoped is not null
            ? CopyObject(ToElement(scoped))
            : new(StringComparer.Ordinal);
        // Explicit native values (including null) win over recovered conversation state.
        if (HistoricalIdentity(request) is { } history)
        {
            if (!raw.ContainsKey("thread_id") && Str(history, "thread_id") is { } thread)
                raw["thread_id"] = thread;
            if (!raw.ContainsKey("user") && endUser is null && Str(history, "user") is { } user)
                raw["user"] = user;
        }
        var (message, attachments) = LastUserMessage(request);
        if (attachments.Count > 0)
        {
            var combined = raw.TryGetValue("attachments", out var existing) && existing is JsonElement { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().Select(item => (object)item.Clone()).ToList()
                : [];
            combined.AddRange(attachments);
            raw["attachments"] = combined;
        }
        var options = new ChatCompletionOptions
        {
            Model = request.Model ?? "",
            Messages = message is null ? [] : [message],
            User = string.IsNullOrWhiteSpace(endUser) ? null : endUser,
            Headers = request.Headers
        };
        options.SetProviderMetadata(new() { [GetIdentifier()] = raw });
        return options;
    }

    private static (ChatMessage? Message, List<object> Attachments) LastUserMessage(AIRequest request)
    {
        var attachments = new List<object>();
        var item = request.Input?.Items?.LastOrDefault(input => string.Equals(input.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return (string.IsNullOrWhiteSpace(request.Input?.Text) ? null : UserMessage(request.Input!.Text!), attachments);
        var parts = new List<object>();
        foreach (var part in item.Content ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    parts.Add(new { type = "text", text = text.Text });
                    break;
                case AIFileContentPart file:
                    var value = StringValue(file.Data)?.Trim()
                        ?? throw new ArgumentException("Xpectrum file inputs require a URL or base64 data.");
                    var mediaType = file.MediaType ?? (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? value[5..Math.Max(5, value.IndexOfAny([';', ',']))] : null);
                    var isUrl = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
                    if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                        parts.Add(new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = isUrl || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                                    ? value : $"data:{mediaType};base64,{value}"
                            }
                        });
                    else if (isUrl)
                        attachments.Add(new { type = AttachmentType(mediaType), url = value });
                    else
                        throw new NotSupportedException(
                            "Xpectrum accepts non-image files by public URL only; pass uploaded files as native attachments with file_id.");
                    break;
            }
        }
        object content = parts is [var single] && JsonSerializer.SerializeToElement(single).GetProperty("type").GetString() == "text"
            ? JsonSerializer.SerializeToElement(single).GetProperty("text").GetString() ?? ""
            : parts.Count == 0 ? "" : parts;
        return (JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.SerializeToElement(new { role = "user", content })), attachments);
    }

    private static ChatMessage UserMessage(string text)
        => JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.SerializeToElement(new { role = "user", content = text }))!;

    private static string AttachmentType(string? mediaType)
        => mediaType?.Split('/')[0].ToLowerInvariant() switch
        {
            "image" => "image",
            "audio" => "audio",
            "video" => "video",
            _ => "document"
        };

    private JsonElement? HistoricalIdentity(AIRequest request)
    {
        var agent = NativeModel(request.Model);
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
            foreach (var part in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
            {
                if (part.ToolName != IdentityTool || part.ProviderExecuted != true || part.Output is null)
                    continue;
                var output = ToElement(part.Output);
                if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("structuredContent", out var structured))
                    output = structured;
                // A thread belongs to the app it was created with.
                if (Str(output, "agent_id") is { } owner && !string.IsNullOrEmpty(agent) && owner != agent)
                    continue;
                if (Str(output, "thread_id") is not null)
                    return output.Clone();
            }
        return null;
    }

    private JsonElement Identity(JsonElement native, ChatCompletionOptions options)
    {
        var scoped = native.TryGetProperty("providerMetadata", out var provider) && provider.TryGetProperty(GetIdentifier(), out var value)
            ? value : default;
        var raw = options.GetProviderMetadata().TryGetValue(GetIdentifier(), out var request) ? ToElement(request) : default;
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["agent_id"] = NativeModel(Str(native, "model") ?? options.Model),
            ["thread_id"] = Str(native, "thread_id") ?? Str(scoped, "thread_id"),
            ["run_id"] = Str(native, "run_id") ?? Str(scoped, "run_id"),
            ["mode"] = Str(native, "mode") ?? Str(scoped, "mode"),
            ["user"] = options.User ?? Str(raw, "user")
        });
    }

    private static object IdentityInput(JsonElement identity)
        => new { agent_id = Str(identity, "agent_id"), thread_id = Str(identity, "thread_id") };

    private static string ToolCallId(JsonElement identity)
        => $"xpectrum-{Str(identity, "run_id") ?? Str(identity, "thread_id") ?? Guid.NewGuid().ToString("N")}";

    private AIToolCallContentPart IdentityPart(JsonElement identity)
        => new()
        {
            Type = "tool-call", ToolCallId = ToolCallId(identity), ToolName = IdentityTool, Title = "Xpectrum thread",
            ProviderExecuted = true, Input = IdentityInput(identity),
            Output = new CallToolResult { StructuredContent = identity }, State = "output-available",
            Metadata = new Dictionary<string, object?> { [GetIdentifier()] = identity }
        };

    private static string CitationId(JsonElement citation)
        => Str(citation, "chunk_id") ?? Str(citation, "document_id") ?? Str(citation, "knowledge_id")
            ?? (citation.TryGetProperty("position", out var position) ? $"citation-{position.GetRawText()}" : "citation");

    private static string CitationTitle(JsonElement citation)
        => Str(citation, "document_name") ?? Str(citation, "knowledge_name") ?? CitationId(citation);

    private Dictionary<string, object?> CitationData(JsonElement citation)
        => new()
        {
            ["sourceId"] = CitationId(citation),
            ["title"] = CitationTitle(citation),
            ["filename"] = Str(citation, "document_name"),
            ["providerMetadata"] = new Dictionary<string, object> { [GetIdentifier()] = citation.Clone() }
        };

    private AIOutputItem CitationItem(JsonElement citation)
        => new()
        {
            Type = "source-document",
            Content = [new AITextContentPart { Type = "text", Text = CitationTitle(citation) }],
            Metadata = new Dictionary<string, object?>
            {
                ["xpectrum.source.id"] = CitationId(citation),
                ["xpectrum.source.citation"] = citation.Clone()
            }
        };

    private AIStreamEvent StreamEvent(string type, string? id, object data, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow },
            Metadata = metadata
        };
}
