using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DocketRouter;

public sealed partial class DocketRouterProvider
{
    private static ChatCompletionOptions PrepareOptions(ChatCompletionOptions options, bool stream)
    {
        // Apply the same provider-scoped root options as the shared transport, before
        // validating the endpoint's narrower schema. Never mutate the caller's request.
        var payload = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web)
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (options.Metadata?.TryGetValue("docketrouter", out var scoped) == true && scoped is not null)
        {
            var providerOptions = JsonSerializer.SerializeToElement(scoped, JsonSerializerOptions.Web);
            foreach (var property in providerOptions.EnumerateObject())
                if (property.Name is not ("headers" or "providerMetadata"))
                    payload[property.Name] = property.Value.Clone();
        }

        payload.Remove("metadata");
        payload.Remove("providerMetadata");
        payload["stream"] = JsonSerializer.SerializeToElement(stream);
        var model = payload["model"].GetString() ?? "";
        payload["model"] = JsonSerializer.SerializeToElement(model.StartsWith("docketrouter/", StringComparison.OrdinalIgnoreCase) ? model[13..] : model);

        if (payload.TryGetValue("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            throw new NotSupportedException("DocketRouter does not support tool calls.");

        var messages = new List<object>();
        foreach (var message in payload["messages"].EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            if (role == "developer") role = "system";
            if (role is not ("system" or "user" or "assistant") || message.TryGetProperty("tool_calls", out _))
                throw new NotSupportedException("DocketRouter accepts only system, user, and assistant text messages.");

            var content = message.GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() : ReadTextParts(content);
            messages.Add(new { role, content = text });
        }
        payload["messages"] = JsonSerializer.SerializeToElement(messages);

        if (!payload.ContainsKey("max_tokens") && payload.TryGetValue("max_completion_tokens", out var maxCompletionTokens))
            payload["max_tokens"] = maxCompletionTokens;
        if (payload.TryGetValue("max_tokens", out var maxTokens) && (maxTokens.GetInt32() <= 0 || maxTokens.GetInt32() > 8192))
            throw new ArgumentOutOfRangeException(nameof(options), "DocketRouter max_tokens must be between 1 and 8192.");

        // These fields are ignored upstream. Omit defaults rather than implying support.
        foreach (var field in new[] { "tools", "tool_choice", "parallel_tool_calls", "store", "stream_options", "reasoning_effort", "max_completion_tokens" })
            payload.Remove(field);

        var outgoing = JsonSerializer.SerializeToElement(payload).Deserialize<ChatCompletionOptions>(JsonSerializerOptions.Web)!;
        outgoing.Headers = options.Headers;
        return outgoing;
    }

    private static string ReadTextParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            throw new NotSupportedException("DocketRouter requires text message content.");
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.GetProperty("type").GetString() is not ("text" or "input_text" or "output_text"))
                throw new NotSupportedException("DocketRouter does not support image, audio, or file content.");
            parts.Add(part.GetProperty("text").GetString() ?? "");
        }
        return string.Concat(parts);
    }

    private static void AddResponseMetadata(Dictionary<string, JsonElement> additional, object? usage,
        Dictionary<string, JsonElement> report)
    {
        if (additional.TryGetValue("docketrouter", out var block) && block.ValueKind == JsonValueKind.Object)
            foreach (var property in block.EnumerateObject())
                report[property.Name] = property.Value.Clone();

        var metadata = additional.TryGetValue("metadata", out var existing) && existing.ValueKind == JsonValueKind.Object
            ? existing.Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web)!
            : new Dictionary<string, object?>();
        if (report.Count > 0)
            metadata["docketrouter"] = JsonSerializer.SerializeToElement(report);
        if (usage is not null)
        {
            var rawUsage = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            metadata["docketrouter.usage"] = rawUsage;
            if (rawUsage.TryGetProperty("cost", out var cost) && cost.TryGetDecimal(out var dollars))
                metadata = ModelCostMetadataEnricher.AddCost(metadata, dollars);
        }
        additional["metadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
    }
}
