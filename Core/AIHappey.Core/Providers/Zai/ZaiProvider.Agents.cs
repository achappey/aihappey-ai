using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Abstractions.Http;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider
{
    private const string AgentsEndpoint = "/api/v1/agents";
    private const string AgentModelPrefix = "agents/";
    private const string GeneralTranslationAgentId = "general_translation";
    private const string ViduTemplateAgentId = "vidu_template_agent";
    private const string SlidesGlmAgentId = "slides_glm_agent";

    private static readonly JsonSerializerOptions AgentJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, ZaiHtmlToolOutputAccumulator> _htmlToolOutputs = new(StringComparer.Ordinal);

    private static bool IsAgentModel(string? model)
        => TryResolveAgentId(model, out _);

    private static bool TryResolveAgentId(string? model, out string agentId)
    {
        agentId = string.Empty;

        if (string.IsNullOrWhiteSpace(model))
            return false;

        var local = model.Trim();
        if (local.StartsWith("zai/", StringComparison.OrdinalIgnoreCase))
            local = local["zai/".Length..];

        if (!local.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        agentId = local[AgentModelPrefix.Length..].Trim();
        return agentId is GeneralTranslationAgentId or ViduTemplateAgentId or SlidesGlmAgentId;
    }

    private async Task<ChatCompletion> CompleteAgentChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryResolveAgentId(options.Model, out var agentId))
            throw new NotSupportedException($"Unsupported Z.AI agent model '{options.Model}'.");

        var capture = options.GetZaiBackendCapture(GetIdentifier());
        var headers = this.SetDefaultChatCompletionProperties(options, ["tools"]);
        var payload = BuildAgentPayload(options, agentId, stream: false);
        using var req = CreateAgentHttpRequest(payload, accept: MediaTypeNames.Application.Json, headers);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Z.AI agent request failed ({(int)resp.StatusCode}): {body}");

        await ProviderBackendCapture.CaptureJsonAsync("agents", resp, body, capture, cancellationToken);

        using var doc = JsonDocument.Parse(body);
        return NormalizeAgentResponse(doc.RootElement, options.Model, agentId);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteAgentChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveAgentId(options.Model, out var agentId))
            throw new NotSupportedException($"Unsupported Z.AI agent model '{options.Model}'.");

        if (agentId == ViduTemplateAgentId)
            throw new NotSupportedException("Z.AI vidu_template_agent creates asynchronous video tasks and is not supported on streaming chat completions.");

        var capture = options.GetZaiBackendCapture(GetIdentifier());
        var headers = this.SetDefaultChatCompletionProperties(options, ["tools"]);
        var payload = BuildAgentPayload(options, agentId, stream: true);
        using var req = CreateAgentHttpRequest(payload, accept: "text/event-stream", headers);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Z.AI agent stream request failed ({(int)resp.StatusCode}): {error}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("agents", resp, capture);
        var completionId = Guid.NewGuid().ToString("n");
        var emittedAnyContent = false;
        var emittedTerminalChunk = false;
        string? activeSlideToolCallId = null;
        string? activeSlideToolName = null;
        var slideToolOrdinal = 0;

        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, cancellationToken);

            var normalizedLine = line.TrimStart('\uFEFF', ' ', '\t');
            if (normalizedLine.Length == 0 || !normalizedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = normalizedLine["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            JsonDocument? evtDoc = null;
            try
            {
                evtDoc = JsonDocument.Parse(data);
                var update = NormalizeAgentStreamResponse(
                    evtDoc.RootElement,
                    options.Model,
                    agentId,
                    ref completionId,
                    ref emittedAnyContent,
                    ref emittedTerminalChunk,
                    ref activeSlideToolCallId,
                    ref activeSlideToolName,
                    ref slideToolOrdinal);
                if (update is not null)
                    yield return update;
            }
            finally
            {
                evtDoc?.Dispose();
            }
        }

        if (!emittedTerminalChunk)
        {
            yield return new ChatCompletionUpdate
            {
                Id = completionId,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = options.Model.ToModelId("zai"),
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = emittedAnyContent ? "stop" : "network_error"
                    }
                ]
            };
        }
    }

    private HttpRequestMessage CreateAgentHttpRequest(
        JsonElement payload,
        string accept,
        IReadOnlyDictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, AgentsEndpoint)
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        ApplyAgentRequestHeaders(req, headers);
        return req;
    }

    private static void ApplyAgentRequestHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static JsonElement BuildAgentPayload(ChatCompletionOptions options, string agentId, bool stream)
    {
        var root = new JsonObject
        {
            ["agent_id"] = agentId,
            ["messages"] = JsonSerializer.SerializeToNode(BuildAgentMessages(options.Messages), AgentJson),
        };

        if (agentId is GeneralTranslationAgentId or SlidesGlmAgentId)
            root["stream"] = stream;

        if (options.AdditionalProperties is not null)
        {
            foreach (var (name, value) in options.AdditionalProperties)
            {
                if (IsIgnoredAgentOption(name))
                    continue;

                root[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        if (!root.ContainsKey("custom_variables") && TryBuildCustomVariablesFromAgentOptions(options.AdditionalProperties, out var customVariables))
            root["custom_variables"] = customVariables;

        return JsonSerializer.SerializeToElement(root, AgentJson);
    }

    private static bool IsIgnoredAgentOption(string name)
        => name is "model" or "tools" or "tool_choice" or "stream_options" or "parallel_tool_calls" or "response_format" or "store" or "metadata";

    private static bool TryBuildCustomVariablesFromAgentOptions(
        Dictionary<string, JsonElement>? additionalProperties,
        out JsonObject customVariables)
    {
        customVariables = [];
        if (additionalProperties is null)
            return false;

        foreach (var field in new[] { "source_lang", "target_lang", "glossary", "strategy", "strategy_config", "template" })
        {
            if (additionalProperties.TryGetValue(field, out var value))
                customVariables[field] = JsonNode.Parse(value.GetRawText());
        }

        return customVariables.Count > 0;
    }

    private static List<object> BuildAgentMessages(IEnumerable<ChatMessage>? messages)
    {
        var result = new List<object>();

        foreach (var message in messages ?? [])
        {
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = BuildAgentContent(message.Content);
            if (content.Count == 0)
                continue;

            result.Add(new
            {
                role = "user",
                content
            });
        }

        if (result.Count == 0)
            throw new InvalidOperationException("Z.AI agents require at least one user message with text or image content.");

        return result;
    }

    private static List<object> BuildAgentContent(JsonElement content)
    {
        var result = new List<object>();

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new { type = "text", text });
                break;
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                    AddAgentContentPart(result, part);
                break;
            case JsonValueKind.Object:
                AddAgentContentPart(result, content);
                break;
        }

        return result;
    }

    private static void AddAgentContentPart(List<object> result, JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
            return;

        var type = TryGetString(part, "type");
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
            && TryGetString(part, "text") is { Length: > 0 } text)
        {
            result.Add(new { type = "text", text });
            return;
        }

        if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
        {
            var imageUrl = TryGetString(part, "image_url");
            if (string.IsNullOrWhiteSpace(imageUrl)
                && part.TryGetProperty("image_url", out var imageUrlObject)
                && imageUrlObject.ValueKind == JsonValueKind.Object)
            {
                imageUrl = TryGetString(imageUrlObject, "url");
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
                result.Add(new { type = "image_url", image_url = imageUrl });
        }
    }

    private static ChatCompletion NormalizeAgentResponse(JsonElement root, string model, string agentId)
    {
        var id = TryGetString(root, "id") ?? TryGetString(root, "async_id") ?? Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (content, finishReason) = agentId switch
        {
            GeneralTranslationAgentId => ExtractTranslationContent(root),
            SlidesGlmAgentId => ExtractSlideContent(root),
            ViduTemplateAgentId => ExtractViduContent(root),
            _ => (root.GetRawText(), "stop")
        };

        return new ChatCompletion
        {
            Id = id,
            Object = "chat.completion",
            Created = created,
            Model = model.ToModelId("zai"),
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content
                    },
                    finish_reason = finishReason
                }
            ],
            Usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            AdditionalProperties = BuildAgentAdditionalProperties(root, agentId)
        };
    }

    private static ChatCompletionUpdate? NormalizeAgentStreamResponse(
        JsonElement root,
        string model,
        string agentId,
        ref string completionId,
        ref bool emittedAnyContent,
        ref bool emittedTerminalChunk,
        ref string? activeSlideToolCallId,
        ref string? activeSlideToolName,
        ref int slideToolOrdinal)
    {
        if (TryGetString(root, "id") is { Length: > 0 } id)
            completionId = id;

        if (agentId == SlidesGlmAgentId && TryNormalizeSlideAgentStreamResponse(
                root,
                model,
                completionId,
                ref activeSlideToolCallId,
                ref activeSlideToolName,
                ref slideToolOrdinal,
                out var slideUpdate))
        {
            emittedAnyContent = true;
            return slideUpdate;
        }

        if (agentId == SlidesGlmAgentId && HasFinishReason(root, "tool_calls"))
        {
            activeSlideToolCallId = null;
            activeSlideToolName = null;
        }

        var (content, finishReason) = agentId == SlidesGlmAgentId
            ? ExtractSlideContent(root, defaultFinishReason: null)
            : ExtractTranslationContent(root, defaultFinishReason: null);

        if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(finishReason))
            return null;

        emittedAnyContent |= !string.IsNullOrEmpty(content);
        emittedTerminalChunk |= !string.IsNullOrEmpty(finishReason);
        return new ChatCompletionUpdate
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model.ToModelId("zai"),
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = "assistant",
                        content = content ?? ""
                    },
                    finish_reason = finishReason
                }
            ],
            Usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            AdditionalProperties = BuildAgentAdditionalProperties(root, agentId)
        };
    }

    private static bool TryNormalizeSlideAgentStreamResponse(
        JsonElement root,
        string model,
        string completionId,
        ref string? activeToolCallId,
        ref string? activeToolName,
        ref int toolOrdinal,
        out ChatCompletionUpdate update)
    {
        update = default!;
        var delta = new Dictionary<string, object?>
        {
            ["role"] = "assistant"
        };
        var hasDelta = false;
        string? emittedToolCallId = null;

        foreach (var choice in EnumerateChoices(root))
        {
            if (!choice.TryGetProperty("messages", out var messages)
                && !choice.TryGetProperty("message", out messages))
            {
                continue;
            }

            foreach (var message in EnumerateSlideMessages(messages))
            {
                var phase = TryGetString(message, "phase");
                if (!message.TryGetProperty("content", out var content))
                    continue;

                if (string.Equals(phase, "thinking", StringComparison.OrdinalIgnoreCase))
                {
                    var reasoning = string.Concat(ExtractTextDeltas(content));
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        delta["reasoning_content"] = reasoning;
                        hasDelta = true;
                    }

                    continue;
                }

                if (string.Equals(phase, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    var toolCalls = BuildSlideToolCallDeltas(root, content, activeToolCallId, activeToolName, toolOrdinal).ToList();
                    if (toolCalls.Count > 0)
                    {
                        if (toolCalls[^1].State is { } toolState)
                        {
                            activeToolCallId = toolState.ToolCallId;
                            activeToolName = toolState.ToolName;
                            toolOrdinal = toolState.ToolOrdinal;
                            emittedToolCallId = toolState.ToolCallId;
                        }

                        delta["tool_calls"] = toolCalls.Select(static toolCall => toolCall.Payload).ToList();
                        hasDelta = true;
                    }

                    continue;
                }

                var text = string.Concat(ExtractTextDeltas(content));
                if (!string.IsNullOrEmpty(text))
                {
                    delta["content"] = text;
                    hasDelta = true;
                }
            }
        }

        if (!hasDelta)
            return false;

        var additionalProperties = BuildAgentAdditionalProperties(root, SlidesGlmAgentId);
        if (!string.IsNullOrWhiteSpace(emittedToolCallId))
            additionalProperties["zai_tool_call_id"] = JsonSerializer.SerializeToElement(emittedToolCallId, AgentJson);

        update = new ChatCompletionUpdate
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model.ToModelId("zai"),
            Choices =
            [
                new
                {
                    index = 0,
                    delta,
                    finish_reason = (string?)null
                }
            ],
            Usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            AdditionalProperties = additionalProperties
        };

        return true;
    }

    private static bool HasFinishReason(JsonElement root, string finishReason)
        => EnumerateChoices(root).Any(choice => string.Equals(TryGetString(choice, "finish_reason"), finishReason, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<AIStreamEvent> MapZaiAgentUnifiedStreamEvents(ChatCompletionUpdate update)
    {
        if (update.AdditionalProperties is null
            || !update.AdditionalProperties.TryGetValue("zai_tool_call_id", out var toolCallIdEl)
            || toolCallIdEl.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        var toolCallId = toolCallIdEl.GetString();
        if (string.IsNullOrWhiteSpace(toolCallId)
            || !update.AdditionalProperties.TryGetValue("zai_agent", out var raw)
            || raw.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var toolOutput in ExtractZaiAgentToolOutputEvents(raw, toolCallId!, update.Id, update.Created))
            yield return toolOutput;
    }

    private IEnumerable<AIStreamEvent> ExtractZaiAgentToolOutputEvents(JsonElement root, string toolCallId, string updateId, long created)
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(created > 0 ? created : DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var choice in EnumerateChoices(root))
        {
            if (!choice.TryGetProperty("messages", out var messages)
                && !choice.TryGetProperty("message", out messages))
            {
                continue;
            }

            foreach (var message in EnumerateSlideMessages(messages))
            {
                if (!string.Equals(TryGetString(message, "phase"), "tool", StringComparison.OrdinalIgnoreCase)
                    || !message.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var part in EnumerateContentParts(content))
                {
                    if (part.ValueKind != JsonValueKind.Object
                        || !part.TryGetProperty("object", out var obj)
                        || obj.ValueKind != JsonValueKind.Object
                        || !obj.TryGetProperty("output", out var outputEl)
                        || outputEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        continue;
                    }

                    var toolName = TryGetString(obj, "tool_name") ?? "zai_agent_tool";
                    var title = TryGetString(obj, "title") ?? TryGetString(part, "title") ?? TryGetString(part, "tag_en") ?? TryGetString(part, "tag_cn");
                    var output = CreateSlideToolOutputCallToolResult(root, toolName, part, obj, outputEl, toolCallId)
                                 ?? new CallToolResult
                                 {
                                     Content = [new TextContentBlock { Text = outputEl.ToString() }],
                                     StructuredContent = outputEl.Clone()
                                 };
                    var providerMetadata = CreateZaiAgentToolProviderMetadata(root, part, toolName, title);
                    var metadata = CreateZaiAgentToolEventMetadata(root, toolName, title);

                    if (TryAccumulateFinalHtmlToolResult(toolCallId, output, out var completedHtmlOutput))
                    {
                        yield return new AIStreamEvent
                        {
                            ProviderId = GetIdentifier(),
                            Event = new AIEventEnvelope
                            {
                                Type = "tool-output-available",
                                Id = toolCallId,
                                Timestamp = timestamp,
                                Data = new AIToolOutputAvailableEventData
                                {
                                    ToolName = toolName,
                                    Output = completedHtmlOutput,
                                    ProviderExecuted = true,
                                    Dynamic = true,
                                    Preliminary = false,
                                    ProviderMetadata = providerMetadata
                                }
                            },
                            Metadata = metadata
                        };

                        if (TryCreateHtmlFileEventData(completedHtmlOutput, providerMetadata, out var completedFileData))
                        {
                            yield return new AIStreamEvent
                            {
                                ProviderId = GetIdentifier(),
                                Event = new AIEventEnvelope
                                {
                                    Type = "file",
                                    Id = toolCallId,
                                    Timestamp = timestamp,
                                    Data = completedFileData
                                },
                                Metadata = metadata
                            };
                        }

                        continue;
                    }

                    var isHtmlDelta = TryExtractHtmlFilePayload(output, out _, out _);

                    yield return new AIStreamEvent
                    {
                        ProviderId = GetIdentifier(),
                        Event = new AIEventEnvelope
                        {
                            Type = "tool-output-available",
                            Id = toolCallId,
                            Timestamp = timestamp,
                            Data = new AIToolOutputAvailableEventData
                            {
                                ToolName = toolName,
                                Output = output,
                                ProviderExecuted = true,
                                Dynamic = true,
                                Preliminary = isHtmlDelta,
                                ProviderMetadata = providerMetadata
                            }
                        },
                        Metadata = metadata
                    };

                    if (!isHtmlDelta && TryCreateHtmlFileEventData(output, providerMetadata, out var fileData))
                    {
                        yield return new AIStreamEvent
                        {
                            ProviderId = GetIdentifier(),
                            Event = new AIEventEnvelope
                            {
                                Type = "file",
                                Id = toolCallId,
                                Timestamp = timestamp,
                                Data = fileData
                            },
                            Metadata = metadata
                        };
                    }
                }
            }
        }
    }

    private Dictionary<string, Dictionary<string, object>> CreateZaiAgentToolProviderMetadata(JsonElement root, JsonElement part, string toolName, string? title)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object>
            {
                ["agent"] = true,
                ["agent_id"] = SlidesGlmAgentId,
                ["tool_name"] = toolName,
                ["title"] = title ?? toolName,
                ["raw"] = root.Clone(),
                ["part"] = part.Clone()
            }
        };

    private static Dictionary<string, object?> CreateZaiAgentToolEventMetadata(JsonElement root, string toolName, string? title)
        => new()
        {
            ["zai.agent"] = true,
            ["zai.agent_id"] = SlidesGlmAgentId,
            ["zai.tool_name"] = toolName,
            ["zai.tool_title"] = title,
            ["zai.agent.raw"] = root.Clone()
        };

    private static bool TryExtractHtmlFilePayload(CallToolResult output, out string url, out string? filename)
    {
        url = string.Empty;
        filename = null;

        if (output.StructuredContent is not { } structuredContent || structuredContent.ValueKind != JsonValueKind.Object)
            return false;

        var mediaType = TryGetString(structuredContent, "media_type") ?? TryGetString(structuredContent, "mediaType");
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        url = TryGetString(structuredContent, "url")
              ?? TryGetString(structuredContent, "resource_uri")
              ?? TryGetString(structuredContent, "resourceUri")
              ?? string.Empty;
        filename = TryGetString(structuredContent, "filename");
        return !string.IsNullOrWhiteSpace(url);
    }

    private static bool TryCreateHtmlFileEventData(
        CallToolResult output,
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        out AIFileEventData fileData)
    {
        fileData = default!;

        if (!TryExtractHtmlFilePayload(output, out var url, out var filename)
            || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        fileData = new AIFileEventData
        {
            MediaType = "text/html",
            Url = url,
            Filename = filename,
            ProviderMetadata = providerMetadata
        };
        return true;
    }

    private static string BuildHtmlDataUrl(string html)
        => $"data:text/html;charset=utf-8;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(html))}";

    private static string NormalizeHtmlText(string html)
        => html
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    private bool TryAccumulateFinalHtmlToolResult(string toolCallId, CallToolResult output, out CallToolResult completedOutput)
    {
        completedOutput = default!;

        if (output.StructuredContent is not { ValueKind: JsonValueKind.Object } structuredContent)
            return false;

        var mediaType = TryGetString(structuredContent, "media_type") ?? TryGetString(structuredContent, "mediaType");
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        var htmlDelta = TryGetString(structuredContent, "html");
        if (string.IsNullOrEmpty(htmlDelta))
            return false;

        var accumulator = _htmlToolOutputs.GetOrAdd(toolCallId, static _ => new ZaiHtmlToolOutputAccumulator());
        var aggregatedHtml = NormalizeHtmlText(accumulator.Append(htmlDelta));
        if (!aggregatedHtml.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase))
            return false;

        _htmlToolOutputs.TryRemove(toolCallId, out _);
        completedOutput = CreateCompletedHtmlToolResult(output, aggregatedHtml);
        return true;
    }

    private static CallToolResult CreateCompletedHtmlToolResult(CallToolResult output, string html)
    {
        if (output.StructuredContent is not { ValueKind: JsonValueKind.Object } structuredContent)
            return output;

        var filename = TryGetString(structuredContent, "filename") ?? "slide.html";
        var resourceUri = TryGetString(structuredContent, "resource_uri")
                          ?? TryGetString(structuredContent, "resourceUri")
                          ?? $"zai://agents/slides/{Uri.EscapeDataString(filename)}";

        return new CallToolResult
        {
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = resourceUri,
                        MimeType = "text/html",
                        Text = html
                    }
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                tool_name = TryGetString(structuredContent, "tool_name"),
                title = TryGetString(structuredContent, "title"),
                position = structuredContent.TryGetProperty("position", out var position) ? position.Clone() : (JsonElement?)null,
                media_type = "text/html",
                filename,
                url = BuildHtmlDataUrl(html),
                resource_uri = resourceUri,
                html
            }, AgentJson)
        };
    }

    private static string BuildHtmlFileName(string? title, string fallback)
    {
        var baseName = string.IsNullOrWhiteSpace(title) ? fallback : title!;
        var builder = new StringBuilder(baseName.Length);
        var lastWasSeparator = false;

        foreach (var ch in baseName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSeparator = false;
                continue;
            }

            if (ch is ' ' or '-' or '_' or '.')
            {
                if (!lastWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }
        }

        var sanitized = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = string.IsNullOrWhiteSpace(fallback) ? "slide" : fallback.Trim();

        return sanitized.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : $"{sanitized}.html";
    }

    private sealed class ZaiHtmlToolOutputAccumulator
    {
        private readonly StringBuilder _buffer = new();

        public string Append(string delta)
        {
            _buffer.Append(delta);
            return _buffer.ToString();
        }
    }

    private static Dictionary<string, JsonElement> BuildAgentAdditionalProperties(JsonElement root, string agentId)
        => new()
        {
            ["agent_id"] = JsonSerializer.SerializeToElement(agentId, AgentJson),
            ["zai_agent"] = root.Clone()
        };

    private static (string Content, string? FinishReason) ExtractTranslationContent(
        JsonElement root,
        string? defaultFinishReason = "stop")
    {
        var chunks = new List<string>();
        string? finishReason = null;

        foreach (var choice in EnumerateChoices(root))
        {
            finishReason ??= TryGetString(choice, "finish_reason");
            if (choice.TryGetProperty("messages", out var messages))
                ExtractTextFromMessages(chunks, messages);

            if (choice.TryGetProperty("message", out var message))
                ExtractTextFromMessages(chunks, message);
        }

        return (string.Join("\n", chunks.Where(static s => !string.IsNullOrWhiteSpace(s))), finishReason ?? defaultFinishReason);
    }

    private static (string Content, string? FinishReason) ExtractSlideContent(
        JsonElement root,
        string? defaultFinishReason = "stop")
    {
        var chunks = new List<string>();
        string? finishReason = null;

        foreach (var choice in EnumerateChoices(root))
        {
            finishReason ??= TryGetString(choice, "finish_reason");
            if (choice.TryGetProperty("message", out var message))
                ExtractSlideTextFromMessage(chunks, message);

            if (choice.TryGetProperty("messages", out var messages))
                ExtractSlideTextFromMessage(chunks, messages);
        }

        return (string.Join("\n", chunks.Where(static s => !string.IsNullOrWhiteSpace(s))), finishReason ?? defaultFinishReason);
    }

    private static (string Content, string? FinishReason) ExtractViduContent(JsonElement root)
    {
        var status = TryGetString(root, "status") ?? "pending";
        var asyncId = TryGetString(root, "async_id");
        var agentId = TryGetString(root, "agent_id") ?? ViduTemplateAgentId;

        return ($"Z.AI video template agent task {status}. agent_id={agentId}; async_id={asyncId}",
            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop");
    }

    private static IEnumerable<JsonElement> EnumerateChoices(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind == JsonValueKind.Object)
                yield return choice;
        }
    }

    private static IEnumerable<JsonElement> EnumerateSlideMessages(JsonElement messages)
    {
        if (messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind == JsonValueKind.Object)
                    yield return message;
            }

            yield break;
        }

        if (messages.ValueKind == JsonValueKind.Object)
            yield return messages;
    }

    private static IEnumerable<SlideToolCallDelta> BuildSlideToolCallDeltas(
        JsonElement root,
        JsonElement content,
        string? activeToolCallId,
        string? activeToolName,
        int toolOrdinal)
    {
        var index = 0;
        foreach (var part in EnumerateContentParts(content))
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("object", out var obj)
                || obj.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var toolName = TryGetString(obj, "tool_name") ?? "zai_agent_tool";
            if (activeToolCallId is null || !string.Equals(activeToolName, toolName, StringComparison.OrdinalIgnoreCase))
            {
                activeToolName = toolName;
                activeToolCallId = BuildSlideToolCallId(root, toolName, part, obj, ++toolOrdinal);
            }

            var inputDelta = TryGetString(obj, "input");
            var output = obj.TryGetProperty("output", out var outputElement)
                ? CreateSlideToolOutputCallToolResult(root, toolName, part, obj, outputElement, activeToolCallId)
                : null;

            if (inputDelta is null && output is null)
                continue;

            yield return new SlideToolCallDelta(
                new
                {
                    index = index++,
                    id = activeToolCallId,
                    type = "function",
                    provider_executed = true,
                    providerExecuted = true,
                    function = new
                    {
                        name = toolName,
                        arguments = inputDelta ?? string.Empty,
                        output,
                        provider_executed = true,
                        providerExecuted = true
                    }
                },
                new SlideToolCallDeltaState(activeToolCallId, activeToolName, toolOrdinal));
        }
    }

    private sealed record SlideToolCallDelta(object Payload, SlideToolCallDeltaState State);

    private sealed record SlideToolCallDeltaState(string ToolCallId, string ToolName, int ToolOrdinal);

    private static string BuildSlideToolCallId(JsonElement root, string toolName, JsonElement part, JsonElement obj, int ordinal)
    {
        var eventId = TryGetString(root, "id");
        var conversationId = TryGetString(root, "conversation_id");
        var title = TryGetString(obj, "title") ?? TryGetString(part, "title") ?? TryGetString(part, "tag_en") ?? TryGetString(part, "tag_cn");
        var position = obj.TryGetProperty("position", out var positionElement) ? positionElement.ToString() : null;
        var suffix = string.Join("-", new[] { eventId, conversationId, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), title, position }.Where(static value => !string.IsNullOrWhiteSpace(value)))
            .Replace(" ", "-", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(suffix)
            ? $"zai-{toolName}"
            : $"zai-{toolName}-{suffix}";
    }

    private static CallToolResult? CreateSlideToolOutputCallToolResult(JsonElement root, string toolName, JsonElement part, JsonElement obj, JsonElement outputElement, string toolCallId)
    {
        if (outputElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var outputText = outputElement.ValueKind == JsonValueKind.String ? outputElement.GetString() ?? string.Empty : outputElement.GetRawText();
        var title = TryGetString(obj, "title") ?? TryGetString(part, "title") ?? TryGetString(part, "tag_en") ?? TryGetString(part, "tag_cn");
        var isHtml = string.Equals(toolName, "add_slide", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(toolName, "insert_page", StringComparison.OrdinalIgnoreCase)
                     || outputText.Contains("<html", StringComparison.OrdinalIgnoreCase);

        if (isHtml)
        {
            outputText = NormalizeHtmlText(outputText);
            var resourceUri = $"zai://agents/slides/{Uri.EscapeDataString(toolCallId)}.html";
            var filename = BuildHtmlFileName(title, toolCallId);
            var dataUrl = BuildHtmlDataUrl(outputText);
            return new CallToolResult
            {
                Content =
                [
                    new EmbeddedResourceBlock
                    {
                        Resource = new TextResourceContents
                        {
                            Uri = resourceUri,
                            MimeType = "text/html",
                            Text = outputText
                        }
                    }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    tool_name = toolName,
                    title,
                    position = obj.TryGetProperty("position", out var position) ? position.Clone() : (JsonElement?)null,
                    media_type = "text/html",
                    filename,
                    url = dataUrl,
                    resource_uri = resourceUri,
                    html = outputText
                }, AgentJson)
            };
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = outputText }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                tool_name = toolName,
                title,
                output = outputElement.Clone()
            }, AgentJson)
        };
    }

    private static void ExtractTextFromMessages(List<string> chunks, JsonElement messages)
    {
        if (messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
                ExtractTextFromMessages(chunks, message);
            return;
        }

        if (messages.ValueKind != JsonValueKind.Object)
            return;

        if (messages.TryGetProperty("content", out var content))
            ExtractTextFromContent(chunks, content);
    }

    private static void ExtractTextFromContent(List<string> chunks, JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                chunks.Add(content.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Object:
                if (TryGetString(content, "text") is { Length: > 0 } text)
                    chunks.Add(text);
                break;
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                    ExtractTextFromContent(chunks, part);
                break;
        }
    }

    private static void ExtractSlideTextFromMessage(List<string> chunks, JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in message.EnumerateArray())
                ExtractSlideTextFromMessage(chunks, item);
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
            return;

        if (message.TryGetProperty("content", out var content))
            ExtractSlideTextFromContent(chunks, content);
    }

    private static void ExtractSlideTextFromContent(List<string> chunks, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
                ExtractSlideTextFromContent(chunks, item);
            return;
        }

        if (content.ValueKind != JsonValueKind.Object)
            return;

        if (TryGetString(content, "text") is { Length: > 0 } text)
            chunks.Add(text);

        if (content.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(obj, "output") is { Length: > 0 } output)
                chunks.Add(output);
        }
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private sealed class ZaiAgentUnifiedStreamState(string providerId, string model, string agentId, string? requestId)
    {
        private readonly string _providerId = providerId;
        private readonly string _model = model.ToModelId("zai");
        private readonly string _agentId = agentId;
        private readonly string _streamId = string.IsNullOrWhiteSpace(requestId) ? $"zai-agent-{Guid.NewGuid():N}" : requestId!;
        private int _toolOrdinal;
        private bool _reasoningActive;
        private bool _textActive;
        private bool _finishEmitted;
        private ZaiAgentToolAccumulator? _activeTool;

        public bool HadContent { get; private set; }

        public IEnumerable<AIStreamEvent> Process(JsonElement root)
        {
            if (TryGetString(root, "id") is { Length: > 0 } id)
                SetStreamId(id);

            foreach (var choice in EnumerateChoices(root))
            {
                var finishReason = TryGetString(choice, "finish_reason");

                if (choice.TryGetProperty("message", out var message))
                {
                    foreach (var streamEvent in ProcessMessages(message, root))
                        yield return streamEvent;
                }

                if (choice.TryGetProperty("messages", out var messages))
                {
                    foreach (var streamEvent in ProcessMessages(messages, root))
                        yield return streamEvent;
                }

                if (string.IsNullOrWhiteSpace(finishReason))
                    continue;

                if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var streamEvent in FlushActiveTool(root, preliminary: false))
                        yield return streamEvent;

                    continue;
                }

                foreach (var streamEvent in Complete(finishReason, root))
                    yield return streamEvent;
            }
        }

        public IEnumerable<AIStreamEvent> Complete(string finishReason, JsonElement? root)
        {
            if (_finishEmitted)
                yield break;

            foreach (var streamEvent in FlushActiveTool(root, preliminary: false))
                yield return streamEvent;

            foreach (var streamEvent in EndReasoning(root))
                yield return streamEvent;

            foreach (var streamEvent in EndText(root))
                yield return streamEvent;

            _finishEmitted = true;
            yield return CreateEvent(
                _streamId,
                "finish",
                new AIFinishEventData
                {
                    FinishReason = finishReason,
                    Model = _model,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        model: _model,
                        timestamp: DateTimeOffset.UtcNow)
                },
                root,
                toolName: null,
                title: null);
        }

        private void SetStreamId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            // The provider run id is stable in normal streams. The request id remains the fallback when no id is emitted.
        }

        private IEnumerable<AIStreamEvent> ProcessMessages(JsonElement messages, JsonElement root)
        {
            if (messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    foreach (var streamEvent in ProcessMessages(message, root))
                        yield return streamEvent;
                }

                yield break;
            }

            if (messages.ValueKind != JsonValueKind.Object)
                yield break;

            var phase = TryGetString(messages, "phase");
            if (!messages.TryGetProperty("content", out var content))
                yield break;

            if (string.Equals(phase, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var delta in ExtractTextDeltas(content))
                {
                    foreach (var streamEvent in EmitReasoningDelta(delta, root))
                        yield return streamEvent;
                }

                yield break;
            }

            if (string.Equals(phase, "tool", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var streamEvent in ProcessToolContent(content, root))
                    yield return streamEvent;

                yield break;
            }

            foreach (var delta in ExtractTextDeltas(content))
            {
                foreach (var streamEvent in EmitTextDelta(delta, root))
                    yield return streamEvent;
            }
        }

        private IEnumerable<AIStreamEvent> EmitReasoningDelta(string delta, JsonElement root)
        {
            if (string.IsNullOrEmpty(delta))
                yield break;

            foreach (var streamEvent in EndText(root))
                yield return streamEvent;

            foreach (var streamEvent in FlushActiveTool(root, preliminary: false))
                yield return streamEvent;

            if (!_reasoningActive)
            {
                _reasoningActive = true;
                yield return CreateEvent(
                    _streamId,
                    "reasoning-start",
                    new AIReasoningStartEventData { ProviderMetadata = CreateProviderMetadata(root, null, null) },
                    root,
                    null,
                    null);
            }

            HadContent = true;
            yield return CreateEvent(
                _streamId,
                "reasoning-delta",
                new AIReasoningDeltaEventData
                {
                    Delta = delta,
                    ProviderMetadata = CreateProviderMetadata(root, null, null)
                },
                root,
                null,
                null);
        }

        private IEnumerable<AIStreamEvent> EmitTextDelta(string delta, JsonElement root)
        {
            if (string.IsNullOrEmpty(delta))
                yield break;

            foreach (var streamEvent in EndReasoning(root))
                yield return streamEvent;

            foreach (var streamEvent in FlushActiveTool(root, preliminary: false))
                yield return streamEvent;

            if (!_textActive)
            {
                _textActive = true;
                yield return CreateEvent(
                    _streamId,
                    "text-start",
                    new AITextStartEventData { ProviderMetadata = CreateLooseProviderMetadata(root, null, null) },
                    root,
                    null,
                    null);
            }

            HadContent = true;
            yield return CreateEvent(
                _streamId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = delta,
                    ProviderMetadata = CreateLooseProviderMetadata(root, null, null)
                },
                root,
                null,
                null);
        }

        private IEnumerable<AIStreamEvent> ProcessToolContent(JsonElement content, JsonElement root)
        {
            foreach (var part in EnumerateContentParts(content))
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !part.TryGetProperty("object", out var obj)
                    || obj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var toolName = TryGetString(obj, "tool_name") ?? _activeTool?.ToolName ?? "zai_agent_tool";
                var title = TryGetString(obj, "title")
                            ?? TryGetString(part, "title")
                            ?? TryGetString(part, "tag_en")
                            ?? TryGetString(part, "tag_cn");

                if (_activeTool is not null
                    && !string.Equals(_activeTool.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
                    && _activeTool.HasAnyPayload)
                {
                    foreach (var streamEvent in FlushActiveTool(root, preliminary: false))
                        yield return streamEvent;
                }

                foreach (var streamEvent in EnsureToolStarted(toolName, title, part, root))
                    yield return streamEvent;

                _activeTool!.UpdateMetadata(part, obj, root);

                if (obj.TryGetProperty("input", out var input))
                {
                    foreach (var streamEvent in EmitToolInput(input, part, root))
                        yield return streamEvent;
                }

                if (obj.TryGetProperty("output", out var output))
                {
                    foreach (var streamEvent in EmitToolOutput(output, part, root))
                        yield return streamEvent;
                }
            }
        }

        private IEnumerable<AIStreamEvent> EnsureToolStarted(string toolName, string? title, JsonElement part, JsonElement root)
        {
            foreach (var streamEvent in EndReasoning(root))
                yield return streamEvent;

            foreach (var streamEvent in EndText(root))
                yield return streamEvent;

            if (_activeTool is not null)
            {
                _activeTool.Title ??= title;
                yield break;
            }

            _activeTool = new ZaiAgentToolAccumulator(
                toolCallId: $"{_streamId}-tool-{++_toolOrdinal}",
                toolName,
                title);

            _activeTool.UpdateMetadata(part, part.TryGetProperty("object", out var obj) ? obj : default, root);
            HadContent = true;

            yield return CreateEvent(
                _activeTool.ToolCallId,
                "tool-input-start",
                new AIToolInputStartEventData
                {
                    ToolName = _activeTool.ToolName,
                    ProviderExecuted = true,
                    Title = _activeTool.Title,
                    ProviderMetadata = CreateProviderMetadata(root, _activeTool.ToolName, _activeTool.Title, part)
                },
                root,
                _activeTool.ToolName,
                _activeTool.Title);
        }

        private IEnumerable<AIStreamEvent> EmitToolInput(JsonElement input, JsonElement part, JsonElement root)
        {
            if (_activeTool is null)
                yield break;

            var delta = input.ValueKind == JsonValueKind.String ? input.GetString() ?? string.Empty : input.GetRawText();
            if (delta.Length == 0)
                yield break;

            _activeTool.AppendInput(delta);
            HadContent = true;

            yield return CreateEvent(
                _activeTool.ToolCallId,
                "tool-input-delta",
                new AIToolInputDeltaEventData { InputTextDelta = delta },
                root,
                _activeTool.ToolName,
                _activeTool.Title);
        }

        private IEnumerable<AIStreamEvent> EmitToolOutput(JsonElement output, JsonElement part, JsonElement root)
        {
            if (_activeTool is null)
                yield break;

            foreach (var streamEvent in EmitToolInputAvailable(root, part))
                yield return streamEvent;

            _activeTool.AppendOutput(output);
            HadContent = true;

            yield return CreateEvent(
                _activeTool.ToolCallId,
                "tool-output-available",
                new AIToolOutputAvailableEventData
                {
                    ToolName = _activeTool.ToolName,
                    Output = _activeTool.CreateToolResult(preliminary: true),
                    ProviderExecuted = true,
                    //Dynamic = true,
                    Preliminary = true,
                    ProviderMetadata = CreateProviderMetadata(root, _activeTool.ToolName, _activeTool.Title, part)
                },
                root,
                _activeTool.ToolName,
                _activeTool.Title);
        }

        private IEnumerable<AIStreamEvent> EmitToolInputAvailable(JsonElement root, JsonElement? part)
        {
            if (_activeTool is null || _activeTool.InputAvailable)
                yield break;

            _activeTool.InputAvailable = true;
            yield return CreateEvent(
                _activeTool.ToolCallId,
                "tool-input-available",
                new AIToolInputAvailableEventData
                {
                    ToolName = _activeTool.ToolName,
                    Input = _activeTool.ParseInput(),
                    ProviderExecuted = true,
                    Title = _activeTool.Title,
                    ProviderMetadata = CreateProviderMetadata(root, _activeTool.ToolName, _activeTool.Title, part)
                },
                root,
                _activeTool.ToolName,
                _activeTool.Title);
        }

        private IEnumerable<AIStreamEvent> FlushActiveTool(JsonElement? root, bool preliminary)
        {
            if (_activeTool is null)
                yield break;

            var eventRoot = root ?? _activeTool.LastRoot ?? default;
            foreach (var streamEvent in EmitToolInputAvailable(eventRoot, _activeTool.LastPart))
                yield return streamEvent;

            if (_activeTool.HasOutput)
            {
                var providerMetadata = CreateProviderMetadata(eventRoot, _activeTool.ToolName, _activeTool.Title, _activeTool.LastPart);
                var toolResult = _activeTool.CreateToolResult(preliminary);

                yield return CreateEvent(
                    _activeTool.ToolCallId,
                    "tool-output-available",
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = _activeTool.ToolName,
                        Output = toolResult,
                        ProviderExecuted = true,
                     //   Dynamic = true,
                        Preliminary = preliminary,
                        ProviderMetadata = providerMetadata
                    },
                    eventRoot,
                    _activeTool.ToolName,
                    _activeTool.Title);

                if (!preliminary && TryCreateHtmlFileEventData(toolResult, providerMetadata, out var fileData))
                {
                    yield return CreateEvent(
                        _activeTool.ToolCallId,
                        "file",
                        fileData,
                        eventRoot,
                        _activeTool.ToolName,
                        _activeTool.Title);
                }
            }

            _activeTool = null;
        }

        private IEnumerable<AIStreamEvent> EndReasoning(JsonElement? root)
        {
            if (!_reasoningActive)
                yield break;

            _reasoningActive = false;
            yield return CreateEvent(
                _streamId,
                "reasoning-end",
                new AIReasoningEndEventData { ProviderMetadata = CreateProviderMetadata(root, null, null) },
                root,
                null,
                null);
        }

        private IEnumerable<AIStreamEvent> EndText(JsonElement? root)
        {
            if (!_textActive)
                yield break;

            _textActive = false;
            yield return CreateEvent(
                _streamId,
                "text-end",
                new AITextEndEventData { ProviderMetadata = CreateLooseProviderMetadata(root, null, null) },
                root,
                null,
                null);
        }

        private AIStreamEvent CreateEvent(string eventId, string type, object data, JsonElement? raw, string? toolName, string? title)
            => new()
            {
                ProviderId = _providerId,
                Event = new AIEventEnvelope
                {
                    Type = type,
                    Id = eventId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Data = data,
                    Metadata = CreateEventMetadata(raw, toolName, title)
                },
                Metadata = CreateEventMetadata(raw, toolName, title)
            };

        private Dictionary<string, object?> CreateEventMetadata(JsonElement? raw, string? toolName, string? title)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["zai.agent"] = true,
                ["zai.agent_id"] = _agentId,
                ["zai.model"] = _model
            };

            if (!string.IsNullOrWhiteSpace(toolName))
                metadata["zai.tool_name"] = toolName;

            if (!string.IsNullOrWhiteSpace(title))
                metadata["zai.tool_title"] = title;

            if (raw is { } rawElement && rawElement.ValueKind is not JsonValueKind.Undefined)
            {
                metadata["zai.agent.raw"] = rawElement.Clone();
                if (TryGetString(rawElement, "conversation_id") is { Length: > 0 } conversationId)
                    metadata["zai.conversation_id"] = conversationId;
            }

            return metadata;
        }

        private Dictionary<string, Dictionary<string, object>>? CreateProviderMetadata(
            JsonElement? raw,
            string? toolName,
            string? title,
            JsonElement? part = null)
        {
            var metadata = new Dictionary<string, object>
            {
                ["agent"] = true,
                ["agent_id"] = _agentId,
                ["model"] = _model
            };

            if (!string.IsNullOrWhiteSpace(toolName))
                metadata["tool_name"] = toolName;

            if (!string.IsNullOrWhiteSpace(title))
                metadata["title"] = title;

            if (raw is { } rawElement && rawElement.ValueKind is not JsonValueKind.Undefined)
            {
                metadata["raw"] = rawElement.Clone();
                if (TryGetString(rawElement, "conversation_id") is { Length: > 0 } conversationId)
                    metadata["conversation_id"] = conversationId;
            }

            if (part is { } partElement && partElement.ValueKind is not JsonValueKind.Undefined)
                metadata["part"] = partElement.Clone();

            return new Dictionary<string, Dictionary<string, object>> { [_providerId] = metadata };
        }

        private Dictionary<string, object>? CreateLooseProviderMetadata(JsonElement? raw, string? toolName, string? title)
            => CreateProviderMetadata(raw, toolName, title)?
                .ToDictionary(static item => item.Key, static item => (object)item.Value);
    }

    private sealed class ZaiAgentToolAccumulator(string toolCallId, string toolName, string? title)
    {
        private readonly StringBuilder _input = new();
        private readonly StringBuilder _textOutput = new();
        private readonly List<JsonElement> _jsonOutputs = [];

        public string ToolCallId { get; } = toolCallId;
        public string ToolName { get; } = toolName;
        public string? Title { get; set; } = title;
        public bool InputAvailable { get; set; }
        public JsonElement? LastRoot { get; private set; }
        public JsonElement? LastPart { get; private set; }
        public JsonElement? Position { get; private set; }
        public bool HasOutput => _textOutput.Length > 0 || _jsonOutputs.Count > 0;
        public bool HasAnyPayload => _input.Length > 0 || HasOutput;

        public void UpdateMetadata(JsonElement part, JsonElement obj, JsonElement root)
        {
            LastPart = part.Clone();
            LastRoot = root.Clone();
            Title ??= TryGetString(obj, "title") ?? TryGetString(part, "title") ?? TryGetString(part, "tag_en") ?? TryGetString(part, "tag_cn");

            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("position", out var position))
                Position = position.Clone();
        }

        public void AppendInput(string delta)
            => _input.Append(delta);

        public void AppendOutput(JsonElement output)
        {
            if (output.ValueKind == JsonValueKind.String)
            {
                _textOutput.Append(output.GetString());
                return;
            }

            if (output.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                _jsonOutputs.Add(output.Clone());
        }

        public object ParseInput()
        {
            var raw = _input.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return new { };

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        public CallToolResult CreateToolResult(bool preliminary)
        {
            if (IsHtmlToolOutput())
                return CreateHtmlToolResult(preliminary);

            if (_textOutput.Length > 0)
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = _textOutput.ToString() }],
                    StructuredContent = CreateStructuredContent(_textOutput.ToString(), preliminary)
                };

            return new CallToolResult
            {
                Content = [],
                StructuredContent = CreateStructuredContent(GetJsonOutput(), preliminary)
            };
        }

        private bool IsHtmlToolOutput()
        {
            if (!string.Equals(ToolName, "add_slide", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ToolName, "insert_page", StringComparison.OrdinalIgnoreCase))
            {
                var value = _textOutput.ToString().TrimStart();
                return value.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                       || value.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("<html", StringComparison.OrdinalIgnoreCase);
            }

            return _textOutput.Length > 0;
        }

        private CallToolResult CreateHtmlToolResult(bool preliminary)
        {
            var html = _textOutput.ToString();
            var resourceUri = BuildHtmlResourceUri();
            var filename = BuildHtmlFileName(Title, BuildSlideId());
            var dataUrl = BuildHtmlDataUrl(html);

            return new CallToolResult
            {
                Content =
                [
                    new EmbeddedResourceBlock
                    {
                        Resource = new TextResourceContents
                        {
                            Uri = resourceUri,
                            MimeType = "text/html",
                            Text = html
                        }
                    }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    tool_name = ToolName,
                    title = Title,
                    position = Position,
                    media_type = "text/html",
                    filename,
                    url = dataUrl,
                    resource_uri = resourceUri,
                    preliminary,
                    html
                }, AgentJson)
            };
        }

        private JsonElement CreateStructuredContent(object? output, bool preliminary)
            => JsonSerializer.SerializeToElement(new
            {
                tool_name = ToolName,
                title = Title,
                position = Position,
                preliminary,
                output
            }, AgentJson);

        private object? GetJsonOutput()
            => _jsonOutputs.Count switch
            {
                0 => null,
                1 => _jsonOutputs[0],
                _ => _jsonOutputs
            };

        private string BuildHtmlResourceUri()
        {
            var conversationId = LastRoot is { } root && TryGetString(root, "conversation_id") is { Length: > 0 } id
                ? id
                : "unknown";
            var slideId = BuildSlideId();
            return $"zai://agents/{Uri.EscapeDataString(conversationId)}/slides/{Uri.EscapeDataString(slideId)}";
        }

        private string BuildSlideId()
        {
            if (Position is { } position && position.ValueKind != JsonValueKind.Undefined)
            {
                if (position.ValueKind == JsonValueKind.Array)
                {
                    var values = position.EnumerateArray().Select(static item => item.ToString()).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
                    if (values.Length > 0)
                        return string.Join("-", values);
                }

                var raw = position.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw;
            }

            if (!string.IsNullOrWhiteSpace(Title))
                return Title!;

            return ToolCallId;
        }
    }

    private static IEnumerable<JsonElement> EnumerateContentParts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
                yield return item;
            yield break;
        }

        yield return content;
    }

    private static IEnumerable<string> ExtractTextDeltas(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                yield return content.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                if (TryGetString(content, "text") is { Length: > 0 } text)
                    yield return text;
                break;
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    foreach (var childText in ExtractTextDeltas(part))
                        yield return childText;
                }

                break;
        }
    }
}
