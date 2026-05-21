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

        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, cancellationToken);

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
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
                    ref emittedTerminalChunk);
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
        ref bool emittedTerminalChunk)
    {
        if (TryGetString(root, "id") is { Length: > 0 } id)
            completionId = id;

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
}
