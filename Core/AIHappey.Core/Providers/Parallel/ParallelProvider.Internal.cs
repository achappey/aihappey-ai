using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private const string ChatCompletionsPath = "chat/completions";
    private const string TaskRunsPath = "v1/tasks/runs";
    private const string ParallelInteractionToolName = "parallel_interaction_context";

    private static readonly HashSet<string> ChatCompletionModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "speed",
        "lite",
        "base",
        "core"
    };

    private static readonly HashSet<string> ResponsesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "parallel"
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };


    private static string NormalizeRole(string? role)
    {
        var value = role?.Trim().ToLowerInvariant();
        return value switch
        {
            "system" => "system",
            "assistant" => "assistant",
            _ => "user"
        };
    }

    private static string FlattenCompletionMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    parts.Add(item.GetRawText());
                    continue;
                }

                var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                if (type == "text" && item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    continue;
                }

                if (item.TryGetProperty("text", out var genericTextEl) && genericTextEl.ValueKind == JsonValueKind.String)
                {
                    var text = genericTextEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(text);
                    continue;
                }

                parts.Add(item.GetRawText());
            }

            return string.Join("\n", parts.Where(a => !string.IsNullOrWhiteSpace(a)));
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? string.Empty;

            return content.GetRawText();
        }

        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : content.GetRawText();
    }

    private bool IsChatCompletionModel(string? model)
    {
        var processor = NormalizeParallelModel(model);

        return !string.IsNullOrWhiteSpace(processor)
               && ChatCompletionModels.Contains(processor);
    }

    private bool IsResponsesModel(string? model)
    {
        var processor = NormalizeParallelModel(model);

        return !string.IsNullOrWhiteSpace(processor)
               && ResponsesModels.Contains(processor);
    }


    private string NormalizeParallelModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[providerPrefix.Length..]
            : trimmed;
    }

    private static AIStreamEvent CreateParallelStreamEvent(
        string providerId,
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static Dictionary<string, Dictionary<string, object>> ToProviderMetadata(string providerId, Dictionary<string, object?> values)
        => new()
        {
            [providerId] = values
                .Where(static item => item.Value is not null)
                .ToDictionary(static item => item.Key, static item => (object)item.Value!)
        };
}

