using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
{
    private async Task<IEnumerable<Model>> ListBytePlusModelsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v3/models");
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"BytePlus models API failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<Model>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var taskTypes = ReadStrings(item, "task_type");
            var inputModalities = ReadNestedStrings(item, "modalities", "input_modalities");
            var outputModalities = ReadNestedStrings(item, "modalities", "output_modalities");
            item.TryGetProperty("token_limits", out var tokenLimits);

            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = ReadString(item, "name") ?? id,
                Object = ReadString(item, "object") ?? "model",
                Created = ReadInt64(item, "created"),
                OwnedBy = "BytePlus",
                Type = InferModelType(taskTypes, inputModalities, outputModalities),
                ContextWindow = ReadInt32(tokenLimits, "context_window"),
                MaxTokens = ReadInt32(tokenLimits, "max_output_token_length"),
                Tags = BuildTags(item, taskTypes, inputModalities, outputModalities)
            });
        }

        return models.WithPricing(GetIdentifier());
    }

    private static string InferModelType(
        IReadOnlyCollection<string> taskTypes,
        IReadOnlyCollection<string> inputModalities,
        IReadOnlyCollection<string> outputModalities)
    {
        if (taskTypes.Any(task => task.Contains("Image", StringComparison.OrdinalIgnoreCase))
            || outputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
        {
            return "image";
        }

        if (taskTypes.Any(task => task.Contains("Video", StringComparison.OrdinalIgnoreCase))
            || outputModalities.Contains("video", StringComparer.OrdinalIgnoreCase))
        {
            return "video";
        }

        if (taskTypes.Any(task => task.Contains("Embedding", StringComparison.OrdinalIgnoreCase)))
            return "embedding";

        if (taskTypes.Any(task => task.Contains("SpeechToText", StringComparison.OrdinalIgnoreCase)))
            return "transcription";

        if (taskTypes.Any(task => task.Contains("TextToSpeech", StringComparison.OrdinalIgnoreCase))
            || outputModalities.Contains("audio", StringComparer.OrdinalIgnoreCase))
        {
            return "speech";
        }

        return "language";
    }

    private static IEnumerable<string> BuildTags(
        JsonElement item,
        IReadOnlyCollection<string> taskTypes,
        IReadOnlyCollection<string> inputModalities,
        IReadOnlyCollection<string> outputModalities)
    {
        var tags = new List<string>();
        AddTag(tags, "status", ReadString(item, "status"));
        AddTag(tags, "domain", ReadString(item, "domain"));
        tags.AddRange(taskTypes.Select(task => $"task:{task}"));
        tags.AddRange(inputModalities.Select(modality => $"input:{modality}"));
        tags.AddRange(outputModalities.Select(modality => $"output:{modality}"));

        if (item.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
        {
            foreach (var feature in features.EnumerateObject())
            {
                if (feature.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var capability in feature.Value.EnumerateObject())
                {
                    if (capability.Value.ValueKind == JsonValueKind.True)
                        tags.Add($"feature:{feature.Name}.{capability.Name}");
                }
            }
        }

        return tags;
    }

    private static void AddTag(List<string> tags, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add($"{name}:{value}");
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.TryGetInt64(out var result)
            ? result
            : null;

    private static int? ReadInt32(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.TryGetInt32(out var result)
            ? result
            : null;

    private static IReadOnlyCollection<string> ReadStrings(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var values)
           && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

    private static IReadOnlyCollection<string> ReadNestedStrings(JsonElement element, string parent, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(parent, out var nested)
            ? ReadStrings(nested, property)
            : [];
}
