using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MindsHub;

public sealed partial class MindsHubProvider
{
    private async Task<IEnumerable<Model>> ListMindsHubModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MindsHub model discovery failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("MindsHub model response must contain a data array.");

            return data.EnumerateArray().Select(ReadModel).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("MindsHub returned an invalid model list.", exception);
        }
    }

    private Model ReadModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
            throw new JsonException("MindsHub returned a model without an ID.");

        var id = idElement.GetString()!;
        var kind = ReadOptionalModelString(item, "kind") ?? ReadOptionalModelString(item, "type");
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = ReadOptionalModelString(item, "name") ?? id,
            OwnedBy = ReadOptionalModelString(item, "owned_by") ?? ReadOptionalModelString(item, "provider") ?? "MindsHub",
            Created = ReadInt64(item, "created"),
            Description = ReadOptionalModelString(item, "description"),
            MaxTokens = ReadInt32(item, "max_output_tokens") ?? ReadInt32(item, "max_tokens"),
            ContextWindow = ReadInt32(item, "context_window")
                ?? ReadInt32(item, "max_input_tokens")
                ?? ReadInt32(item, "context_length"),
            Type = MapModelType(kind),
            Tags = ReadStringArray(item, "tags")
        };
    }

    private static string? ReadOptionalModelString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"MindsHub model field '{property}' must be a string or null.");
        return value.GetString();
    }

    private static string MapModelType(string? kind)
        => kind?.ToLowerInvariant() switch
        {
            "decision" => "decision",
            "embedding" => "embedding",
            "image" => "image",
            "speech" => "speech",
            "transcription" => "transcription",
            "video" => "video",
            _ => "language"
        };

    private static int? ReadInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static long? ReadInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static IEnumerable<string>? ReadStringArray(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : null;
}
