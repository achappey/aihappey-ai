using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Models;

// Internal boundaries for existing raw JSON/legacy adapters. Known fields become typed;
// provider-specific fields remain in the contracts' extension data.
internal static class ChatCompletionContractAdapters
{
    internal static T ReadChatContract<T>(this JsonElement value)
        => value.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new JsonException($"Expected {typeof(T).Name}.");

    internal static ChatCompletion ReadProviderChatCompletion(this JsonElement raw)
    {
        // Native provider telemetry is not OpenAI's string-valued metadata.
        // Keep it as raw extension data, as the existing cost enrichers do.
        if (raw.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.EnumerateObject().Any(p => p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            var properties = raw.EnumerateObject().Where(p => p.Name != "metadata")
                .ToDictionary(p => p.Name, p => p.Value);
            var completion = JsonSerializer.SerializeToElement(properties).ReadChatContract<ChatCompletion>();
            (completion.AdditionalProperties ??= [])["metadata"] = metadata.Clone();
            return completion;
        }
        return raw.ReadChatContract<ChatCompletion>();
    }

    internal static ChatCompletionUsage? ToChatCompletionUsage(this object? value)
    {
        if (value is null)
            return null;
        if (value is ChatCompletionUsage typed)
            return typed;
        if (value is AIUsage { AdditionalProperties: { Count: > 0 } rawUsage } normalized)
        {
            var preserved = JsonSerializer.SerializeToElement(rawUsage).ToChatCompletionUsage()!;
            preserved.PromptTokens = normalized.InputTokens ?? preserved.PromptTokens;
            preserved.CompletionTokens = normalized.OutputTokens ?? preserved.CompletionTokens;
            preserved.TotalTokens = normalized.TotalTokens ?? preserved.TotalTokens;
            return preserved;
        }
        if (value is AIUsage unified)
            return new ChatCompletionUsage
            {
                PromptTokens = unified.InputTokens ?? 0,
                CompletionTokens = unified.OutputTokens ?? 0,
                TotalTokens = unified.TotalTokens ?? ((unified.InputTokens ?? 0) + (unified.OutputTokens ?? 0)),
                PromptTokensDetails = unified.CachedInputTokens is null && unified.CacheWriteInputTokens is null
                    ? null
                    : new ChatCompletionPromptTokenDetails
                    {
                        CachedTokens = unified.CachedInputTokens,
                        CacheWriteTokens = unified.CacheWriteInputTokens
                    },
                CompletionTokensDetails = unified.ReasoningTokens is null
                    ? null
                    : new ChatCompletionCompletionTokenDetails { ReasoningTokens = unified.ReasoningTokens },
                AdditionalProperties = new()
                {
                    ["input_tokens"] = JsonSerializer.SerializeToElement(unified.InputTokens),
                    ["output_tokens"] = JsonSerializer.SerializeToElement(unified.OutputTokens)
                }
            };

        var raw = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var usage = raw.ReadChatContract<ChatCompletionUsage>();
        // Some legacy/native adapters use Responses token names. Retain those raw
        // properties as well as populating the Chat Completions token fields.
        if (!raw.TryGetProperty("prompt_tokens", out _) && raw.TryGetProperty("input_tokens", out var input))
            usage.PromptTokens = input.ValueKind == JsonValueKind.Null ? 0 : input.GetInt32();
        if (!raw.TryGetProperty("completion_tokens", out _) && raw.TryGetProperty("output_tokens", out var output))
            usage.CompletionTokens = output.ValueKind == JsonValueKind.Null ? 0 : output.GetInt32();
        return usage;
    }

    internal static ChatCompletionDelta ToChatCompletionDelta(this object value)
    {
        if (value is ChatCompletionDelta typed)
            return typed;
        var raw = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        var delta = raw.ReadChatContract<ChatCompletionDelta>();
        if (delta.ToolCalls is not null && raw.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var (call, source) in delta.ToolCalls.Zip(calls.EnumerateArray()))
            {
                if (!source.TryGetProperty("index", out _))
                    call.Index = index;
                index++;
            }
        }
        return delta;
    }

    internal static ChatCompletionResponseFormat? ToChatCompletionResponseFormat(this object? value)
        => value is null ? null : value is ChatCompletionResponseFormat format ? format
            : (value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web))
                .ReadChatContract<ChatCompletionResponseFormat>();

    internal static ChatCompletionStreamChoice ToChatCompletionStreamChoice(this Dictionary<string, object?> value)
    {
        var choice = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web).ReadChatContract<ChatCompletionStreamChoice>();
        if (value.TryGetValue("delta", out var delta) && delta is not null)
            choice.Delta = delta.ToChatCompletionDelta();
        return choice;
    }

    internal static void SetProviderMetadata(this ChatCompletionOptions options, Dictionary<string, object?>? metadata)
    {
        if (metadata is not null)
            (options.AdditionalProperties ??= [])["providerMetadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
    }

    internal static Dictionary<string, object?> GetProviderMetadata(this ChatCompletionOptions options)
    {
        var metadata = new Dictionary<string, object?>();
        if (options.AdditionalProperties is not { } additional)
            return metadata;

        foreach (var (key, value) in additional)
            if (key != "providerMetadata")
                metadata[key] = value;

        if (additional.TryGetValue("providerMetadata", out var providers) && providers.ValueKind == JsonValueKind.Object)
            foreach (var property in providers.EnumerateObject())
                metadata[property.Name] = property.Value;

        return metadata;
    }
}
