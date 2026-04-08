using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var payload = MapChatCompletionsRequestToNative(options, stream: false);
        var native = await SendNativeAsync(payload, cancellationToken);
        return MapNativeToChatCompletion(native, options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = MapChatCompletionsRequestToNative(options, stream: true);
        await foreach (var chunk in SendNativeStreamingAsync(payload, cancellationToken))
            yield return MapNativeToChatCompletionUpdate(chunk, options.Model);
    }

    private static object MapChatCompletionsRequestToNative(ChatCompletionOptions options, bool stream)
    {
        var messages = options.Messages.ToCortecsMessages().ToList();
        var tools = options.Tools is { } t && t.Any()
            ? t.ToCortecsTools().ToList()
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["response_format"] = options.ResponseFormat,
            ["tools"] = tools,
            ["tool_choice"] = options.ToolChoice,
            ["parallel_tool_calls"] = options.ParallelToolCalls
        };

        // ChatCompletionOptions is intentionally minimal, but callers may provide extended
        // provider-specific fields that we can preserve by reading the serialized shape.
        var root = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web);

        AddIfPresent(root, payload, "preference");
        AddIfPresent(root, payload, "allowed_providers");
        AddIfPresent(root, payload, "eu_native");
        AddIfPresent(root, payload, "allow_quantization");
        AddIfPresent(root, payload, "max_tokens");
        AddIfPresent(root, payload, "top_p");
        AddIfPresent(root, payload, "frequency_penalty");
        AddIfPresent(root, payload, "presence_penalty");
        AddIfPresent(root, payload, "stop");
        AddIfPresent(root, payload, "logprobs");
        AddIfPresent(root, payload, "seed");
        AddIfPresent(root, payload, "n");
        AddIfPresent(root, payload, "prediction");
        AddIfPresent(root, payload, "safe_prompt");

        return payload;
    }

    private static ChatCompletion MapNativeToChatCompletion(JsonElement native, string fallbackModel)
    {
        var id = native.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = native.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = native.TryGetProperty("created", out var cEl) && cEl.TryGetInt64(out var epoch)
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        object? usage = null;
        if (native.TryGetProperty("usage", out var uEl))
            usage = JsonSerializer.Deserialize<object>(uEl.GetRawText(), JsonSerializerOptions.Web);

        object[] choices = [];
        if (native.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
            choices = JsonSerializer.Deserialize<object[]>(chEl.GetRawText(), JsonSerializerOptions.Web) ?? [];

        return new ChatCompletion
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Object = native.TryGetProperty("object", out var oEl) && oEl.ValueKind == JsonValueKind.String
                ? (oEl.GetString() ?? "chat.completion")
                : "chat.completion",
            Created = created,
            Model = model ?? fallbackModel,
            Choices = choices,
            Usage = usage
        };
    }

    private static ChatCompletionUpdate MapNativeToChatCompletionUpdate(JsonElement native, string fallbackModel)
    {
        var id = native.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = native.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = native.TryGetProperty("created", out var cEl) && cEl.TryGetInt64(out var epoch)
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        object? usage = null;
        if (native.TryGetProperty("usage", out var uEl))
            usage = JsonSerializer.Deserialize<object>(uEl.GetRawText(), JsonSerializerOptions.Web);

        object[] choices = [];
        if (native.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
            choices = JsonSerializer.Deserialize<object[]>(chEl.GetRawText(), JsonSerializerOptions.Web) ?? [];

        return new ChatCompletionUpdate
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Object = native.TryGetProperty("object", out var oEl) && oEl.ValueKind == JsonValueKind.String
                ? (oEl.GetString() ?? "chat.completion.chunk")
                : "chat.completion.chunk",
            Created = created,
            Model = model ?? fallbackModel,
            Choices = choices,
            Usage = usage
        };
    }
}

