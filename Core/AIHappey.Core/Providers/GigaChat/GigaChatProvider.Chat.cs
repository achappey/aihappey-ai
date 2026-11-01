using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.GigaChat;

public sealed partial class GigaChatProvider
{
    private (JsonElement Payload, IReadOnlyDictionary<string, string>? Headers) BuildChatRequest(
        ChatCompletionOptions options,
        bool streaming)
    {
        var defaultHeaders = this.SetDefaultChatCompletionProperties(options);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (defaultHeaders is not null)
            foreach (var (name, value) in defaultHeaders)
                headers[name] = value;
        if (options.Headers is not null)
            foreach (var (name, value) in options.Headers)
                headers[name] = value;

        var serialized = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serialized.EnumerateObject())
            properties[property.Name] = property.Value.Clone();

        properties.Remove("metadata");
        properties.Remove("providerMetadata");
        if (options.GetProviderMetadata().TryGetValue(GetIdentifier(), out var providerOptions) &&
            providerOptions is not null)
        {
            var providerJson = providerOptions is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(providerOptions, JsonSerializerOptions.Web);
            if (providerJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in providerJson.EnumerateObject())
                {
                    if (property.Name is "headers" or "tools" or "providerMetadata")
                        continue;
                    properties[property.Name] = property.Value.Clone();
                }
            }
        }

        var payload = JsonNode.Parse(JsonSerializer.Serialize(properties, JsonSerializerOptions.Web))!.AsObject();
        if (options.Tools?.Any() == true && payload["tools"] is JsonArray tools)
        {
            var functions = new JsonArray();
            foreach (var tool in tools)
            {
                if (tool is not JsonObject toolObject ||
                    !string.Equals(toolObject["type"]?.GetValue<string>(), "function", StringComparison.Ordinal) ||
                    toolObject["function"] is not JsonObject function)
                    throw new NotSupportedException("GigaChat v1 supports function tools only.");

                functions.Add(function.DeepClone());
            }

            payload.Remove("tools");
            payload["functions"] = functions;
        }
        else if (options.AdditionalProperties?.ContainsKey("tools") != true)
        {
            payload.Remove("tools");
        }

        MapToolChoice(payload);
        MapToolMessages(payload);
        if (streaming)
            payload["stream"] = true;

        using var document = JsonDocument.Parse(payload.ToJsonString(JsonSerializerOptions.Web));
        return (document.RootElement.Clone(), headers.Count == 0 ? null : headers);
    }

    private static void MapToolChoice(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("tool_choice", out var choice) || choice is null)
        {
            payload.Remove("tool_choice");
            return;
        }

        payload.Remove("tool_choice");
        if (choice is JsonValue value && value.TryGetValue<string>(out var stringChoice))
        {
            if (stringChoice is not ("auto" or "none"))
                throw new NotSupportedException($"GigaChat v1 does not support tool_choice '{stringChoice}'.");
            payload["function_call"] = stringChoice;
            return;
        }

        if (choice is JsonObject choiceObject &&
            string.Equals(choiceObject["type"]?.GetValue<string>(), "function", StringComparison.Ordinal) &&
            choiceObject["function"] is JsonObject function &&
            function["name"] is JsonNode name)
        {
            payload["function_call"] = new JsonObject { ["name"] = name.DeepClone() };
            return;
        }

        throw new NotSupportedException("GigaChat v1 supports only auto, none, or a named function choice.");
    }

    private static void MapToolMessages(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages)
            return;

        var functionNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = message["role"]?.GetValue<string>();
            if (string.Equals(role, "assistant", StringComparison.Ordinal) &&
                message["tool_calls"] is JsonArray toolCalls)
            {
                if (toolCalls.Count != 1)
                    throw new NotSupportedException("GigaChat v1 supports one function call per assistant message.");

                if (toolCalls[0] is not JsonObject toolCall ||
                    toolCall["function"] is not JsonObject function ||
                    function["name"]?.GetValue<string>() is not { Length: > 0 } name)
                    throw new InvalidOperationException("A chat tool call must include a function name.");

                if (toolCall["id"]?.GetValue<string>() is { Length: > 0 } callId)
                    functionNamesByCallId[callId] = name;

                var functionCall = new JsonObject { ["name"] = name };
                if (function.TryGetPropertyValue("arguments", out var arguments) && arguments is not null)
                    functionCall["arguments"] = ParseFunctionArguments(arguments);

                message.Remove("tool_calls");
                message["function_call"] = functionCall;
            }
            else if (string.Equals(role, "tool", StringComparison.Ordinal))
            {
                var callId = message["tool_call_id"]?.GetValue<string>();
                if (callId is null || !functionNamesByCallId.TryGetValue(callId, out var functionName))
                    throw new InvalidOperationException(
                        "A tool result must reference a preceding assistant function call.");

                message.Remove("tool_call_id");
                message["role"] = "function";
                message["name"] = functionName;
            }
        }
    }

    private static JsonNode ParseFunctionArguments(JsonNode arguments)
    {
        if (arguments is JsonValue value && value.TryGetValue<string>(out var json))
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JsonObject();
            return JsonNode.Parse(json)
                ?? throw new JsonException("Function-call arguments must contain a JSON value.");
        }

        return arguments.DeepClone();
    }

    private static string NormalizeFunctionCallResponse(string json, string callId, bool streaming)
    {
        var payload = JsonNode.Parse(json)?.AsObject()
            ?? throw new JsonException("GigaChat returned an invalid chat completion.");
        if (payload["choices"] is JsonArray choices)
        {
            foreach (var choice in choices.OfType<JsonObject>())
            {
                if (string.Equals(
                        choice["finish_reason"]?.GetValue<string>(),
                        "function_call",
                        StringComparison.Ordinal))
                    choice["finish_reason"] = "tool_calls";

                var messageType = streaming ? "delta" : "message";
                if (choice[messageType] is not JsonObject message ||
                    message["function_call"] is not JsonObject functionCall)
                    continue;

                message.Remove("function_call");
                var function = new JsonObject();
                if (functionCall["name"] is JsonNode name)
                    function["name"] = name.DeepClone();
                if (functionCall.TryGetPropertyValue("arguments", out var arguments) && arguments is not null)
                    function["arguments"] = arguments is JsonValue argumentValue &&
                                            argumentValue.TryGetValue<string>(out var stringArguments)
                        ? stringArguments
                        : arguments.ToJsonString(JsonSerializerOptions.Web);

                message["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["id"] = callId,
                    ["type"] = "function",
                    ["function"] = function
                });
            }
        }

        return payload.ToJsonString(JsonSerializerOptions.Web);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> ReadChatUpdatesAsync(
        HttpClient client,
        JsonElement payload,
        IReadOnlyDictionary<string, string>? headers,
        string callId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in client.GetChatCompletionSseEvents(
                           payload,
                           relativeUrl: "v1/chat/completions",
                           headers: headers,
                           ct: cancellationToken))
        {
            if (string.IsNullOrEmpty(streamEvent.Data))
                continue;
            yield return ParseUpdate(streamEvent.Data);
        }

        ChatCompletionUpdate ParseUpdate(string data)
        {
            try
            {
                var normalized = NormalizeFunctionCallResponse(data, callId, streaming: true);
                var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(
                    normalized, JsonSerializerOptions.Web)
                    ?? throw new JsonException("GigaChat returned an empty streaming event.");
                if (!string.IsNullOrEmpty(update.Model))
                    update.Model = $"{GetIdentifier()}/{update.Model}";
                if (update.Created == 0)
                    update.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return update;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Failed to parse GigaChat SSE event: {data}", exception);
            }
        }
    }
}
