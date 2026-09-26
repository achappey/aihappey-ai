using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private static string? GetRouterPreference(string? model) => model switch
    {
        "speed" or "balanced" or "cost" => model,
        _ => null
    };

    private void ApplyRouterPreference(ChatCompletionOptions options)
    {
        var preference = GetRouterPreference(options.Model);
        if (preference is not null)
            ApplyRouterPreference(options.Metadata ??= [], GetIdentifier(), preference);
    }

    private void ApplyRouterPreference(ResponseRequest options)
    {
        var preference = GetRouterPreference(options.Model);
        if (preference is not null)
            ApplyRouterPreference(options.Metadata ??= [], GetIdentifier(), preference);
    }

    private static void ApplyRouterPreference(Dictionary<string, object?> metadata, string providerId, string preference)
    {
        // Both payload builders apply provider metadata last. Preserve existing Cortecs options,
        // but make the selected virtual route authoritative over any model/preference override.
        var providerOptions = metadata.TryGetValue(providerId, out var existing) && existing is not null
            ? JsonSerializer.SerializeToNode(existing) as JsonObject ?? new JsonObject()
            : new JsonObject();

        providerOptions["preference"] = preference;
        providerOptions["model"] = null;
        metadata[providerId] = JsonSerializer.SerializeToElement(providerOptions);
    }
}
