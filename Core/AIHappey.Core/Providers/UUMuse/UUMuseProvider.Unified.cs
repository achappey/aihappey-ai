using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.UUMuse;

public partial class UUMuseProvider
{
    private const string UUMuseDefaultModel = "google/gemini-2.5-flash";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var prompt = BuildUUMuseQuestion(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("UUMuse ask requires non-empty text from the unified request.");

        var providerMetadata = GetUUMuseProviderMetadata(request);
        var (modelId, workspaceId) = ResolveUUMuseModelAndWorkspace(request, providerMetadata);
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidOperationException("UUMuse requires a workspace id. Provide metadata.uumuse.workspace_id or use a model shortcut like 'uumuse/{modelId}@{workspaceId}'.");

        var response = await AskUUMuseAsync(prompt, modelId, workspaceId, providerMetadata, cancellationToken);

        return ToUnifiedResponse(request, response, modelId, workspaceId, prompt);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;
        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"uumuse_{Guid.NewGuid():N}";
        var text = ExtractOutputText(response.Output);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return CreateUUMuseStreamEvent(providerId, "text-start", eventId, new AITextStartEventData(), timestamp, response.Metadata);
            yield return CreateUUMuseStreamEvent(providerId, "text-delta", eventId, new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
            yield return CreateUUMuseStreamEvent(providerId, "text-end", eventId, new AITextEndEventData(), timestamp, response.Metadata);
        }

        foreach (var source in ExtractSources(response.Output))
            yield return CreateSourceStreamEvent(providerId, source, timestamp, response.Metadata);

        yield return CreateUUMuseStreamEvent(
            providerId,
            "finish",
            eventId,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                InputTokens = TryGetUsageInt(response.Usage, "prompt_tokens", "input_tokens"),
                OutputTokens = TryGetUsageInt(response.Usage, "completion_tokens", "output_tokens"),
                TotalTokens = TryGetUsageInt(response.Usage, "total_tokens"),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? UUMuseDefaultModel,
                    timestamp,
                    usage: response.Usage,
                    inputTokens: TryGetUsageInt(response.Usage, "prompt_tokens", "input_tokens"),
                    outputTokens: TryGetUsageInt(response.Usage, "completion_tokens", "output_tokens"),
                    totalTokens: TryGetUsageInt(response.Usage, "total_tokens"),
                    temperature: request.Temperature)
            },
            timestamp,
            response.Metadata);
    }

    private async Task<UUMuseAskResponse> AskUUMuseAsync(
        string question,
        string modelId,
        string workspaceId,
        UUMuseProviderMetadata? providerMetadata,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["question"] = question,
            ["workspace_id"] = workspaceId
        };

        if (!string.IsNullOrWhiteSpace(modelId))
            payload["model_id"] = modelId;

        if (providerMetadata?.AdditionalProperties is not null)
        {
            foreach (var property in providerMetadata.AdditionalProperties)
            {
                if (property.Key is "question" or "workspace_id" or "workspaceId" or "model_id" or "modelId")
                    continue;

                payload[property.Key] = JsonSerializer.Deserialize<object>(property.Value.GetRawText(), JsonSerializerOptions.Web);
            }
        }

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/ask")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"UUMuse ask failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ToUUMuseAskResponse(doc.RootElement);
    }

    private AIResponse ToUnifiedResponse(
        AIRequest request,
        UUMuseAskResponse response,
        string requestedModel,
        string workspaceId,
        string question)
    {
        var model = string.IsNullOrWhiteSpace(response.ModelId) ? requestedModel : response.ModelId!;
        var outputItems = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = response.Answer ?? string.Empty
                    }
                ]
            }
        };

        outputItems.AddRange(response.Citations.Select(CreateSourceOutputItem));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToUnifiedModelId(model, workspaceId),
            Status = "completed",
            Usage = response.Usage is JsonElement usage
                ? ToPlainObject(usage)
                : new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = 0,
                    ["completion_tokens"] = 0,
                    ["total_tokens"] = 0
                },
            Output = new AIOutput { Items = outputItems },
            Metadata = BuildUnifiedResponseMetadata(request, response, model, workspaceId, question)
        };
    }

    private static string BuildUUMuseQuestion(AIRequest request)
    {
        var lastUserText = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => ExtractUnifiedText(item.Content))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (!string.IsNullOrWhiteSpace(lastUserText))
            return lastUserText!;

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            parts.Add(request.Instructions!);

        parts.AddRange((request.Input?.Items ?? [])
            .Select(item => ExtractUnifiedText(item.Content))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.Join("\n\n", parts);
    }

    private static string ExtractUnifiedText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? []).OfType<AITextContentPart>().Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));

    private UUMuseProviderMetadata? GetUUMuseProviderMetadata(AIRequest request)
        => request.Metadata.GetProviderMetadata<UUMuseProviderMetadata>(GetIdentifier());

    private static (string ModelId, string? WorkspaceId) ResolveUUMuseModelAndWorkspace(
        AIRequest request,
        UUMuseProviderMetadata? providerMetadata)
    {
        var model = request.Model ?? UUMuseDefaultModel;
        if (model.StartsWith("uumuse/", StringComparison.OrdinalIgnoreCase))
            model = model["uumuse/".Length..];

        string? workspaceId = null;
        var atIndex = model.LastIndexOf('@');
        if (atIndex > 0 && atIndex < model.Length - 1)
        {
            workspaceId = model[(atIndex + 1)..].Trim();
            model = model[..atIndex];
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
            workspaceId = providerMetadata?.WorkspaceId;

        if (!string.IsNullOrWhiteSpace(providerMetadata?.ModelId)
            && (string.IsNullOrWhiteSpace(request.Model) || string.Equals(model, UUMuseDefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            model = providerMetadata.ModelId!;
        }

        if (string.IsNullOrWhiteSpace(model))
            model = UUMuseDefaultModel;

        return (model, workspaceId);
    }

    private static string ToUnifiedModelId(string modelId, string workspaceId)
        => $"uumuse/{modelId}@{workspaceId}";

    private static UUMuseAskResponse ToUUMuseAskResponse(JsonElement root)
    {
        var citations = new List<UUMuseCitation>();
        if (root.TryGetProperty("citations", out var citationsEl) && citationsEl.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var citationEl in citationsEl.EnumerateArray())
            {
                if (citationEl.ValueKind != JsonValueKind.Object)
                    continue;

                citations.Add(new UUMuseCitation
                {
                    FileName = GetUUMuseString(citationEl, "file_name") ?? GetUUMuseString(citationEl, "fileName"),
                    FileId = GetUUMuseString(citationEl, "file_id") ?? GetUUMuseString(citationEl, "fileId"),
                    ChunkIndex = GetUUMuseInt(citationEl, "chunk_index") ?? GetUUMuseInt(citationEl, "chunkIndex"),
                    ContentPreview = GetUUMuseString(citationEl, "content_preview") ?? GetUUMuseString(citationEl, "contentPreview"),
                    SourceUrl = GetUUMuseString(citationEl, "source_url") ?? GetUUMuseString(citationEl, "sourceUrl") ?? GetUUMuseString(citationEl, "url"),
                    Raw = citationEl.Clone(),
                    Ordinal = index++
                });
            }
        }

        JsonElement? usage = root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? usageEl.Clone()
            : null;

        return new UUMuseAskResponse
        {
            Answer = GetUUMuseString(root, "answer"),
            ModelId = GetUUMuseString(root, "model_id") ?? GetUUMuseString(root, "modelId"),
            Usage = usage,
            Citations = citations,
            Raw = root.Clone()
        };
    }

    private static AIOutputItem CreateSourceOutputItem(UUMuseCitation citation)
    {
        var sourceId = GetCitationSourceId(citation);
        var url = citation.SourceUrl ?? sourceId;
        var title = citation.FileName ?? citation.SourceUrl ?? citation.FileId ?? $"Citation {citation.Ordinal + 1}";

        return new AIOutputItem
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = title
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = url,
                ["chatcompletions.source.title"] = title,
                ["messages.source.url"] = url,
                ["messages.source.title"] = title,
                ["uumuse.citation.file_name"] = citation.FileName,
                ["uumuse.citation.file_id"] = citation.FileId,
                ["uumuse.citation.chunk_index"] = citation.ChunkIndex,
                ["uumuse.citation.content_preview"] = citation.ContentPreview,
                ["uumuse.citation.source_url"] = citation.SourceUrl,
                ["uumuse.citation.raw"] = citation.Raw
            }
        };
    }

    private static AIStreamEvent CreateSourceStreamEvent(
        string providerId,
        UUMuseSourceRecord source,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "source-url",
                Id = source.Url,
                Timestamp = timestamp,
                Data = new AISourceUrlEventData
                {
                    SourceId = source.SourceId,
                    Url = source.Url,
                    Title = source.Title,
                    Type = "file_citation",
                    Filename = source.FileName,
                    FileId = source.FileId,
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, source.Metadata ?? [])
                }
            },
            Metadata = metadata
        };

    private static IEnumerable<UUMuseSourceRecord> ExtractSources(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            if (!string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase))
                continue;

            var metadata = item.Metadata;
            var url = TryGetMetadataString(metadata, "chatcompletions.source.url")
                ?? TryGetMetadataString(metadata, "messages.source.url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            yield return new UUMuseSourceRecord(
                SourceId: url!,
                Url: url!,
                Title: TryGetMetadataString(metadata, "chatcompletions.source.title") ?? TryGetMetadataString(metadata, "messages.source.title"),
                FileName: TryGetMetadataString(metadata, "uumuse.citation.file_name"),
                FileId: TryGetMetadataString(metadata, "uumuse.citation.file_id"),
                Metadata: metadata);
        }
    }

    private static string ExtractOutputText(AIOutput? output)
        => string.Concat(
            (output?.Items ?? [])
                .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text));

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(
        AIRequest request,
        UUMuseAskResponse response,
        string model,
        string workspaceId,
        string question)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["uumuse.model_id"] = model,
            ["uumuse.workspace_id"] = workspaceId,
            ["uumuse.question"] = question,
            ["uumuse.response.raw"] = response.Raw
        };

        if (response.Usage is JsonElement usage)
            metadata["uumuse.usage"] = usage.Clone();

        if (response.Citations.Count > 0)
            metadata["uumuse.citations"] = response.Citations.Select(ToCitationDto).ToList();

        return metadata;
    }

    private static object ToCitationDto(UUMuseCitation citation)
        => new
        {
            file_name = citation.FileName,
            file_id = citation.FileId,
            chunk_index = citation.ChunkIndex,
            content_preview = citation.ContentPreview,
            source_url = citation.SourceUrl
        };

    private static string GetCitationSourceId(UUMuseCitation citation)
        => !string.IsNullOrWhiteSpace(citation.SourceUrl)
            ? citation.SourceUrl!
            : !string.IsNullOrWhiteSpace(citation.FileId)
                ? $"uumuse://files/{citation.FileId}#chunk={citation.ChunkIndex ?? citation.Ordinal}"
                : $"uumuse://citations/{citation.Ordinal}";

    private static AIStreamEvent CreateUUMuseStreamEvent(
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

    private static Dictionary<string, Dictionary<string, object>>? CreateScopedProviderMetadata(
        string providerId,
        Dictionary<string, object?> values)
    {
        var filtered = values
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);

        return filtered.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
            {
                [providerId] = filtered
            };
    }

    private static string? GetUUMuseString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => property.ToString()
        };
    }

    private static int? GetUUMuseInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static int? TryGetUsageInt(object? usage, params string[] names)
    {
        if (usage is null)
            return null;

        try
        {
            var element = usage is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);

            if (element.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var property))
                    continue;

                if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric))
                    return numeric;

                if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
                    return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetMetadataString(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata?.TryGetValue(key, out var value) != true || value is null)
            return null;

        return value switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json when json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    private static object? ToPlainObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), JsonSerializerOptions.Web);

    private sealed class UUMuseProviderMetadata
    {
        [JsonPropertyName("workspace_id")]
        public string? WorkspaceId { get; set; }

        [JsonPropertyName("model_id")]
        public string? ModelId { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }

    private sealed class UUMuseAskResponse
    {
        public string? Answer { get; init; }

        public string? ModelId { get; init; }

        public JsonElement? Usage { get; init; }

        public List<UUMuseCitation> Citations { get; init; } = [];

        public JsonElement Raw { get; init; }
    }

    private sealed class UUMuseCitation
    {
        public string? FileName { get; init; }

        public string? FileId { get; init; }

        public int? ChunkIndex { get; init; }

        public string? ContentPreview { get; init; }

        public string? SourceUrl { get; init; }

        public JsonElement Raw { get; init; }

        public int Ordinal { get; init; }
    }

    private sealed record UUMuseSourceRecord(
        string SourceId,
        string Url,
        string? Title,
        string? FileName,
        string? FileId,
        Dictionary<string, object?>? Metadata);
}
