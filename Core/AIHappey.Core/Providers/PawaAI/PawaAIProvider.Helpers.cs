using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    private static readonly JsonSerializerOptions PawaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private string NormalizePawaModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var value = model.Trim().Trim('/');
        var prefix = GetIdentifier() + "/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    private static JsonObject CopyPawaOptions(JsonElement options)
    {
        var result = new JsonObject();
        if (options.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in options.EnumerateObject())
            result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return result;
    }

    private JsonElement GetPawaOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null
           && providerOptions.TryGetValue(GetIdentifier(), out var options)
           && options.ValueKind == JsonValueKind.Object
            ? options
            : default;

    private JsonElement GetPawaOptions(Dictionary<string, object?>? metadata)
        => metadata.GetProviderMetadata<JsonElement>(GetIdentifier());

    private async Task<IReadOnlyList<PawaAgentDefinition>> ListPawaAgentsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/agents/view");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return JsonSerializer.Deserialize<List<PawaAgentDefinition>>(data.GetRawText(), PawaJson) ?? [];
    }

    private static void EnsurePawaSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"PawaAI {operation} failed ({(int)response.StatusCode}): {raw}");
    }

    private static string PawaFileDataAsString(AIFileContentPart file)
        => file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => file.Data?.ToString() ?? string.Empty
        };

    private static PawaFile DecodePawaFile(AIFileContentPart file, int index)
    {
        var value = PawaFileDataAsString(file).Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"PawaAI file {index + 1} is empty.", nameof(file));
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType!;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"PawaAI file {index + 1} must use a base64 data URL.", nameof(file));
            var header = value[5..comma];
            var separator = header.IndexOf(';');
            if (separator > 0) mediaType = header[..separator];
            value = value[(comma + 1)..];
        }
        var bytes = Convert.FromBase64String(value);
        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        return new PawaFile(filename, mediaType, bytes);
    }

    private AIStreamEvent CreatePawaEvent(
        string id,
        string type,
        object data,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata },
            Metadata = metadata
        };

    private AIStreamEvent CreatePawaFinishEvent(AIRequest request, AIResponse response)
    {
        var now = DateTimeOffset.UtcNow;
        return CreatePawaEvent(request.Id ?? Guid.NewGuid().ToString("N"), "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = now.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? request.Model ?? string.Empty, now, response.Usage)
        }, response.Metadata);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamPawaBufferedResponse(
        AIRequest request,
        AIResponse response,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        yield return CreatePawaEvent(id, "text-start", new AITextStartEventData(), response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreatePawaEvent(id, "text-delta", new AITextDeltaEventData { Delta = text }, response.Metadata);
        yield return CreatePawaEvent(id, "text-end", new AITextEndEventData(), response.Metadata);
        yield return CreatePawaFinishEvent(request, response);
        await Task.CompletedTask;
    }

    private sealed class PawaAgentDefinition
    {
        public long Id { get; set; }
        public string? AgentReferenceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;
        public List<string> Intents { get; set; } = [];
        public long? KnowledgeBaseId { get; set; }
        public JsonElement Tools { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed record PawaFile(string Filename, string MediaType, byte[] Bytes);
}
