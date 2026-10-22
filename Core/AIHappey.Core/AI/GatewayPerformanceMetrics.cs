using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>Request-local, gateway-observed streaming TTFT (seconds) and output tokens/second.</summary>
public static class GatewayPerformanceMetrics
{
    public static IAsyncEnumerable<UIMessagePart> Measure(
        Func<IAsyncEnumerable<UIMessagePart>> stream,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
        => MeasureCore(stream, Observe, timeProvider, cancellationToken);

    public static IAsyncEnumerable<ChatCompletionUpdate> Measure(
        Func<IAsyncEnumerable<ChatCompletionUpdate>> stream,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
        => MeasureCore(stream, Observe, timeProvider, cancellationToken);

    public static IAsyncEnumerable<ResponseStreamPart> Measure(
        Func<IAsyncEnumerable<ResponseStreamPart>> stream,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
        => MeasureCore(stream, Observe, timeProvider, cancellationToken);

    public static IAsyncEnumerable<MessageStreamPart> Measure(
        Func<IAsyncEnumerable<MessageStreamPart>> stream,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
        => MeasureCore(stream, Observe, timeProvider, cancellationToken);

    private static async IAsyncEnumerable<T> MeasureCore<T>(
        Func<IAsyncEnumerable<T>> stream, Func<Tracker, T, T> observe, TimeProvider? timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Allocate per enumeration, before invoking the provider (including synchronous setup).
        var tracker = new Tracker(timeProvider ?? TimeProvider.System);
        await foreach (var part in stream().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return observe(tracker, part);
        }
    }

    private static UIMessagePart Observe(Tracker tracker, UIMessagePart part)
    {
        tracker.Content(part switch
        {
            TextDeltaUIMessageStreamPart text => !string.IsNullOrEmpty(text.Delta),
            ReasoningDeltaUIPart reasoning => !string.IsNullOrEmpty(reasoning.Delta),
            ToolCallDeltaPart tool => !string.IsNullOrEmpty(tool.InputTextDelta),
            _ => false
        });

        if (part is not FinishUIPart { MessageMetadata: { } metadata } finish)
            return part;

        tracker.OutputTokens(metadata.Usage.CompletionTokens);
        if (!tracker.HasContent)
            return part;

        return new FinishUIPart
        {
            Type = finish.Type,
            FinishReason = finish.FinishReason,
            MessageMetadata = metadata with { ProviderMetadata = tracker.Merge(metadata.ProviderMetadata) }
        };
    }

    private static ChatCompletionUpdate Observe(Tracker tracker, ChatCompletionUpdate part)
    {
        var choices = JsonSerializer.SerializeToElement(part.Choices, JsonSerializerOptions.Web);
        var hasContent = false;
        var hasFinish = false;
        foreach (var choice in choices.EnumerateArray())
        {
            hasFinish |= Property(choice, "finish_reason").ValueKind == JsonValueKind.String;
            var delta = Property(choice, "delta");
            hasContent |= HasText(Property(delta, "content"))
                || HasText(Property(delta, "reasoning_content"))
                || HasText(Property(delta, "reasoning"))
                || HasText(Property(delta, "thinking"))
                || HasText(Property(delta, "refusal"))
                || HasText(Property(Property(delta, "function_call"), "arguments"));

            var calls = Property(delta, "tool_calls");
            if (calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                    hasContent |= HasText(Property(Property(call, "function"), "arguments"))
                        || HasText(Property(Property(call, "custom"), "input"));
            }
        }

        tracker.Content(hasContent);
        tracker.OutputTokens(ReadOutputTokens(part.Usage));
        var metadata = part.AdditionalProperties?.GetValueOrDefault("metadata") ?? default;
        // Usage and cost can arrive after the finish-reason chunk. Enrich those tails too,
        // without buffering, adding events, or extending the content timing interval.
        if (tracker.HasContent && (hasFinish || part.Usage is not null
            || Property(metadata, "gateway").ValueKind == JsonValueKind.Object))
        {
            part.AdditionalProperties = part.AdditionalProperties is null ? [] : new(part.AdditionalProperties);
            part.AdditionalProperties["metadata"] = JsonSerializer.SerializeToElement(tracker.Merge(ToDictionary(metadata)));
        }
        return part;
    }

    private static MessageStreamPart Observe(Tracker tracker, MessageStreamPart part)
    {
        tracker.Content(part.Type == "content_block_delta" && part.Delta is { } delta && (delta.Type switch
        {
            "text_delta" => !string.IsNullOrEmpty(delta.Text),
            "thinking_delta" => !string.IsNullOrEmpty(delta.Thinking),
            "input_json_delta" => !string.IsNullOrEmpty(delta.PartialJson),
            _ => false
        }));

        // message_start usage is preliminary (often output_tokens: 0), not final usage.
        if (part.Type == "message_delta")
            tracker.OutputTokens(part.Usage?.OutputTokens);

        if (tracker.HasContent && part.Type is "message_delta" or "message_stop")
            part.Metadata = tracker.Merge(part.Metadata);
        return part;
    }

    private static ResponseStreamPart Observe(Tracker tracker, ResponseStreamPart part)
    {
        tracker.Content(part switch
        {
            ResponseOutputTextDelta text => !string.IsNullOrEmpty(text.Delta),
            ResponseReasoningTextDelta reasoning => !string.IsNullOrEmpty(reasoning.Delta),
            ResponseReasoningSummaryTextDelta reasoning => !string.IsNullOrEmpty(reasoning.Delta),
            ResponseRefusalDelta refusal => !string.IsNullOrEmpty(refusal.Delta),
            ResponseFunctionCallArgumentsDelta tool => !string.IsNullOrEmpty(tool.Delta),
            ResponseMcpCallArgumentsDelta tool => !string.IsNullOrEmpty(tool.Delta),
            ResponseCustomToolCallInputDelta tool => !string.IsNullOrEmpty(tool.Delta),
            ResponseCodeInterpreterCallCodeDelta tool => !string.IsNullOrEmpty(tool.Delta),
            ResponseShellCallCommandDelta tool => !string.IsNullOrEmpty(tool.Delta),
            _ => false
        });

        if (part is ResponseCompleted completed)
        {
            tracker.OutputTokens(ReadOutputTokens(completed.Response.Usage));
            if (tracker.HasContent)
                completed.Response.Metadata = tracker.Merge(completed.Response.Metadata);
        }
        // Incomplete is a valid terminal response (e.g. max_output_tokens). It currently
        // uses the unknown-event envelope; preserve its raw fields while adding metadata.
        else if (part is ResponseUnknownEvent { Type: "response.incomplete", Data: { } data }
            && data.TryGetValue("response", out var response))
        {
            tracker.OutputTokens(ReadOutputTokens(Property(response, "usage")));
            if (tracker.HasContent)
            {
                var fields = ToDictionary(response);
                fields["metadata"] = JsonSerializer.SerializeToElement(tracker.Merge(ToDictionary(Property(response, "metadata"))));
                data["response"] = JsonSerializer.SerializeToElement(fields);
            }
        }
        return part;
    }

    private static long? ReadOutputTokens(object? usage)
    {
        if (usage is null)
            return null;
        var json = usage is JsonElement element ? element : JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
        foreach (var key in new[] { "completion_tokens", "output_tokens", "completionTokens", "outputTokens" })
        {
            var value = Property(json, key);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens >= 0)
                return tokens;
        }
        return null;
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static bool HasText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return !string.IsNullOrEmpty(value.GetString());
        if (value.ValueKind != JsonValueKind.Array)
            return false;
        // Only actual text parts, never arbitrary serialized content or metadata.
        return value.EnumerateArray().Any(part => Property(part, "type").GetString() is "text" or "output_text" or "reasoning_text"
            && HasText(Property(part, "text")));
    }

    private static Dictionary<string, JsonElement> ToDictionary(object? value)
    {
        if (value is null)
            return [];
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        return json.ValueKind == JsonValueKind.Object
            ? json.Deserialize<Dictionary<string, JsonElement>>() ?? [] : [];
    }

    private sealed class Tracker(TimeProvider clock)
    {
        private readonly long _started = clock.GetTimestamp();
        private long? _first;
        private long _last;
        private long? _outputTokens;

        public bool HasContent => _first.HasValue;

        public void Content(bool hasContent)
        {
            if (!hasContent)
                return;
            _last = clock.GetTimestamp();
            _first ??= _last;
        }

        public void OutputTokens(long? tokens)
        {
            if (tokens >= 0)
                _outputTokens = tokens;
        }

        public Dictionary<string, JsonElement> Merge(Dictionary<string, JsonElement>? metadata)
        {
            var result = metadata is null ? [] : new Dictionary<string, JsonElement>(metadata);
            var gateway = ToDictionary(result.GetValueOrDefault("gateway"));
            gateway["ttft"] = JsonSerializer.SerializeToElement(clock.GetElapsedTime(_started, _first!.Value).TotalSeconds);
            var seconds = clock.GetElapsedTime(_first.Value, _last).TotalSeconds;
            if (_outputTokens.HasValue && seconds > 0)
            {
                var tps = _outputTokens.Value / seconds;
                if (double.IsFinite(tps))
                    gateway["tps"] = JsonSerializer.SerializeToElement(tps);
            }
            result["gateway"] = JsonSerializer.SerializeToElement(gateway);
            return result;
        }

        public Dictionary<string, object?> Merge(Dictionary<string, object?>? metadata)
            => Merge(ToDictionary(metadata)).ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
    }
}
