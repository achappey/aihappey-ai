using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    private void NormalizeResponseOptions(ResponseRequest options)
    {
        if (options.Metadata?.TryGetValue(GetIdentifier(), out var value) != true || value is null)
            return;

        var config = JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web) as JsonObject
            ?? throw new ArgumentException("DeepSeek provider metadata must be a JSON object.");

        // Existing saved chat settings used Chat Completions' thinking/reasoning_effort.
        // Never overwrite an explicitly supplied Responses reasoning configuration.
        if (!config.ContainsKey("reasoning") && options.Reasoning is null)
        {
            var disabled = config["thinking"] is JsonObject thinking
                && thinking["type"]?.GetValue<string>() == "disabled";
            if (disabled || config.ContainsKey("reasoning_effort") || config.ContainsKey("thinking"))
                config["reasoning"] = new JsonObject
                {
                    ["effort"] = disabled ? "none" : config["reasoning_effort"]?.GetValue<string>() ?? "high"
                };
        }

        config.Remove("thinking");
        config.Remove("reasoning_effort");
        options.Metadata = new Dictionary<string, object?>(options.Metadata!)
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(config)
        };
    }
}
