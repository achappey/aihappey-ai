using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.SambaNova;

public partial class SambaNovaProvider
{
    private const string SambaNovaAgentModelPrefix = "agent/";
    private const string SambaNovaAgentsBaseUrl = "https://chat.sambanova.ai/api/agent/";
    private const string SambaNovaMainAgentId = "mainagent";
    private const string SambaNovaCodingAgentId = "coding";
    private const string SambaNovaDatascienceAgentId = "datascience";
    private const string SambaNovaDeepresearchAgentId = "deepresearch";
    private const string SambaNovaFinancialAnalysisAgentId = "financialanalysis";

    private static readonly JsonSerializerOptions SambaNovaAgentJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private sealed record SambaNovaAgentFile(string Name, string MediaType, byte[] Content);

    private static bool IsSambaNovaAgentModel(string? model)
        => TryResolveSambaNovaAgentId(model, out _);

    private static bool TryResolveSambaNovaAgentId(string? model, out string agentId)
    {
        agentId = string.Empty;
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var local = model.Trim().Trim('/');
        if (local.StartsWith("sambanova/", StringComparison.OrdinalIgnoreCase))
            local = local["sambanova/".Length..].Trim('/');

        if (!local.StartsWith(SambaNovaAgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        agentId = local[SambaNovaAgentModelPrefix.Length..].Trim('/').ToLowerInvariant();
        return agentId is SambaNovaMainAgentId
            or SambaNovaCodingAgentId
            or SambaNovaDatascienceAgentId
            or SambaNovaDeepresearchAgentId
            or SambaNovaFinancialAnalysisAgentId;
    }

    private async Task<AIResponse> ExecuteAgentUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveSambaNovaAgentId(request.Model, out var agentId))
            throw new NotSupportedException($"Unsupported SambaNova agent model '{request.Model}'.");

        ApplyAuthHeader();

        var interactive = ShouldUseSambaNovaAgentInteractiveEndpoint(request);
        using var httpRequest = agentId == SambaNovaDatascienceAgentId
            ? CreateSambaNovaDatascienceAgentRequest(request, agentId, interactive, out var requestPayload)
            : CreateSambaNovaJsonAgentRequest(request, agentId, interactive, out requestPayload);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"SambaNova agent '{agentId}' request failed ({(int)response.StatusCode})."
                : $"SambaNova agent '{agentId}' request failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return CreateSambaNovaAgentUnifiedResponse(request, agentId, interactive, requestPayload, document.RootElement.Clone());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamp = DateTimeOffset.UtcNow;
        var response = await ExecuteAgentUnifiedAsync(request, cancellationToken);
        var eventId = request.Id ?? $"sambanova_agent_{Guid.NewGuid():N}";
        var metadata = response.Metadata ?? [];
        var text = ExtractSambaNovaAgentResponseText(response);
        var providerMetadata = CreateSambaNovaAgentLooseProviderMetadata(response);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return CreateSambaNovaAgentStreamEvent(
                eventId,
                "text-start",
                new AITextStartEventData { ProviderMetadata = providerMetadata },
                timestamp,
                metadata);

            yield return CreateSambaNovaAgentStreamEvent(
                eventId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = text,
                    ProviderMetadata = providerMetadata
                },
                DateTimeOffset.UtcNow,
                metadata);

            yield return CreateSambaNovaAgentStreamEvent(
                eventId,
                "text-end",
                new AITextEndEventData { ProviderMetadata = providerMetadata },
                DateTimeOffset.UtcNow,
                metadata);
        }

        yield return CreateSambaNovaAgentFinishEvent(eventId, request, response, DateTimeOffset.UtcNow, metadata);
    }

    private HttpRequestMessage CreateSambaNovaJsonAgentRequest(
        AIRequest request,
        string agentId,
        bool interactive,
        out JsonElement requestPayload)
    {
        var payload = BuildSambaNovaJsonAgentPayload(request, agentId, interactive);
        requestPayload = JsonSerializer.SerializeToElement(payload, SambaNovaAgentJson);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildSambaNovaAgentUri(agentId, interactive))
        {
            Content = new StringContent(payload.ToJsonString(SambaNovaAgentJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        return httpRequest;
    }

    private HttpRequestMessage CreateSambaNovaDatascienceAgentRequest(
        AIRequest request,
        string agentId,
        bool interactive,
        out JsonElement requestPayload)
    {
        var fields = BuildSambaNovaDatascienceAgentFields(request, interactive);
        var files = ExtractSambaNovaAgentFiles(request).ToList();
        fields["files_count"] = files.Count;
        if (files.Count > 0)
            fields["files"] = new JsonArray(files.Select(file => JsonValue.Create(file.Name)).ToArray<JsonNode?>());

        var form = new MultipartFormDataContent();
        foreach (var field in fields)
        {
            if (field.Key is "files" or "files_count")
                continue;

            var value = ConvertSambaNovaAgentFormValue(field.Value);
            if (value is not null)
                form.Add(new StringContent(value, Encoding.UTF8), field.Key);
        }

        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Content);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
            form.Add(content, "files", file.Name);
        }

        requestPayload = JsonSerializer.SerializeToElement(fields, SambaNovaAgentJson);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildSambaNovaAgentUri(agentId, interactive))
        {
            Content = form
        };

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        return httpRequest;
    }

    private static Uri BuildSambaNovaAgentUri(string agentId, bool interactive)
        => new($"{SambaNovaAgentsBaseUrl}{Uri.EscapeDataString(agentId)}{(interactive ? "/interactive" : string.Empty)}");

    private static JsonObject BuildSambaNovaJsonAgentPayload(AIRequest request, string agentId, bool interactive)
    {
        var providerOptions = ExtractSambaNovaProviderOptions(request.Metadata);
        var payload = providerOptions is null ? [] : JsonElementObjectToJsonObject(providerOptions.Value);

        var prompt = ExtractSambaNovaAgentPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = TryGetSambaNovaJsonString(payload, "prompt");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("SambaNova agent requests require a prompt, user message, or input text.", nameof(request));

        payload["prompt"] = prompt;

        if (agentId == SambaNovaCodingAgentId && !HasSambaNovaJsonProperty(payload, "code"))
        {
            var code = ExtractSambaNovaAgentCode(request);
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("SambaNova coding agent requests require provider metadata 'code' or a file content part with code.", nameof(request));

            payload["code"] = code;
        }

        ApplySambaNovaInteractiveAliases(payload, interactive);
        return payload;
    }

    private static JsonObject BuildSambaNovaDatascienceAgentFields(AIRequest request, bool interactive)
    {
        var providerOptions = ExtractSambaNovaProviderOptions(request.Metadata);
        var fields = providerOptions is null ? [] : JsonElementObjectToJsonObject(providerOptions.Value);

        var prompt = ExtractSambaNovaAgentPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = TryGetSambaNovaJsonString(fields, "prompt");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("SambaNova data science agent requests require a prompt, user message, or input text.", nameof(request));

        fields["prompt"] = prompt;
        ApplySambaNovaInteractiveAliases(fields, interactive);
        return fields;
    }

    private static void ApplySambaNovaInteractiveAliases(JsonObject payload, bool interactive)
    {
        if (!interactive)
            return;

        if (!HasSambaNovaJsonProperty(payload, "thread_id") && TryGetSambaNovaJsonNode(payload, "threadId", out var threadId))
            payload["thread_id"] = threadId?.DeepClone();

        if (!HasSambaNovaJsonProperty(payload, "file_ids_json") && TryGetSambaNovaJsonNode(payload, "fileIdsJson", out var fileIdsJson))
            payload["file_ids_json"] = fileIdsJson?.DeepClone();
    }

    private static bool ShouldUseSambaNovaAgentInteractiveEndpoint(AIRequest request)
    {
        var providerOptions = ExtractSambaNovaProviderOptions(request.Metadata);
        if (providerOptions is { ValueKind: JsonValueKind.Object }
            && ContainsAnySambaNovaAgentProperty(providerOptions.Value, "resume", "thread_id", "threadId", "file_ids_json", "fileIdsJson"))
        {
            return true;
        }

        return request.Metadata is not null
               && ContainsAnySambaNovaAgentMetadataKey(request.Metadata, "resume", "thread_id", "threadId", "file_ids_json", "fileIdsJson");
    }

    private AIResponse CreateSambaNovaAgentUnifiedResponse(
        AIRequest request,
        string agentId,
        bool interactive,
        JsonElement requestPayload,
        JsonElement root)
    {
        var text = ExtractSambaNovaAgentResponseText(root);
        var providerStatus = root.TryGetString("status");
        var metadata = CreateSambaNovaAgentMetadata(agentId, interactive, requestPayload, root, providerStatus);
        var model = NormalizeSambaNovaAgentResponseModel(request.Model, agentId);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = NormalizeSambaNovaAgentStatus(providerStatus),
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["sambanova.agent.raw"] = root.Clone(),
                                    ["sambanova.agent.artifacts"] = CloneSambaNovaAgentProperty(root, "artifacts"),
                                    ["sambanova.agent.file_ids"] = CloneSambaNovaAgentProperty(root, "file_ids"),
                                    ["sambanova.agent.thread_id"] = root.TryGetString("thread_id")
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["sambanova.agent.raw"] = root.Clone(),
                            ["sambanova.agent.status"] = providerStatus,
                            ["sambanova.agent.thread_id"] = root.TryGetString("thread_id"),
                            ["sambanova.agent.interrupt"] = CloneSambaNovaAgentProperty(root, "interrupt")
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["sambanova.agent.raw"] = root.Clone(),
                    ["sambanova.agent.artifacts"] = CloneSambaNovaAgentProperty(root, "artifacts"),
                    ["sambanova.agent.file_ids"] = CloneSambaNovaAgentProperty(root, "file_ids")
                }
            }
        };
    }

    private Dictionary<string, object?> CreateSambaNovaAgentMetadata(
        string agentId,
        bool interactive,
        JsonElement requestPayload,
        JsonElement root,
        string? providerStatus)
        => new()
        {
            ["sambanova.agent"] = true,
            ["sambanova.agent.id"] = agentId,
            ["sambanova.agent.endpoint"] = $"agent/{agentId}{(interactive ? "/interactive" : string.Empty)}",
            ["sambanova.agent.interactive"] = interactive,
            ["sambanova.agent.status"] = providerStatus,
            ["sambanova.agent.request.payload"] = requestPayload.Clone(),
            ["sambanova.agent.raw"] = root.Clone(),
            ["sambanova.agent.result"] = root.TryGetString("result"),
            ["sambanova.agent.thread_id"] = root.TryGetString("thread_id"),
            ["sambanova.agent.artifacts"] = CloneSambaNovaAgentProperty(root, "artifacts"),
            ["sambanova.agent.file_ids"] = CloneSambaNovaAgentProperty(root, "file_ids"),
            ["sambanova.agent.interrupt"] = CloneSambaNovaAgentProperty(root, "interrupt"),
            ["sambanova.agent.content_type"] = root.TryGetString("content_type"),
            ["sambanova.agent.title"] = root.TryGetString("title")
        };

    private static AIStreamEvent CreateSambaNovaAgentStreamEvent(
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = "sambanova",
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateSambaNovaAgentFinishEvent(
        string eventId,
        AIRequest request,
        AIResponse response,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        var model = response.Model ?? request.Model ?? "agent";
        var finishReason = string.Equals(response.Status, "requires_action", StringComparison.OrdinalIgnoreCase)
            ? "interrupt"
            : string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? "error"
                : "stop";

        return CreateSambaNovaAgentStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = finishReason,
                Model = model,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model,
                    timestamp,
                    response.Usage,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["sambanova"] = metadata.TryGetValue("sambanova.agent.raw", out var raw) ? raw : null
                    })
            },
            timestamp,
            metadata);
    }

    private static string NormalizeSambaNovaAgentResponseModel(string? model, string agentId)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model.StartsWith("sambanova/", StringComparison.OrdinalIgnoreCase)
                ? model
                : model.ToModelId("sambanova");

        return $"{SambaNovaAgentModelPrefix}{agentId}".ToModelId("sambanova");
    }

    private static string NormalizeSambaNovaAgentStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "success" => "completed",
            "completed" => "completed",
            "interrupt" => "requires_action",
            "failed" or "error" => "failed",
            { Length: > 0 } value => value,
            _ => "completed"
        };

    private static Dictionary<string, object>? CreateSambaNovaAgentLooseProviderMetadata(AIResponse response)
    {
        var metadata = new Dictionary<string, object>();

        if (response.Metadata?.TryGetValue("sambanova.agent.raw", out var raw) == true && raw is not null)
            metadata["raw"] = raw;

        if (response.Metadata?.TryGetValue("sambanova.agent.artifacts", out var artifacts) == true && artifacts is not null)
            metadata["artifacts"] = artifacts;

        if (response.Metadata?.TryGetValue("sambanova.agent.file_ids", out var fileIds) == true && fileIds is not null)
            metadata["file_ids"] = fileIds;

        if (response.Metadata?.TryGetValue("sambanova.agent.thread_id", out var threadId) == true && threadId is not null)
            metadata["thread_id"] = threadId;

        return metadata.Count == 0 ? null : metadata;
    }

    private static string ExtractSambaNovaAgentResponseText(AIResponse response)
        => string.Join("\n", (response.Output?.Items ?? [])
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string ExtractSambaNovaAgentResponseText(JsonElement root)
    {
        if (root.TryGetString("result") is { Length: > 0 } result)
            return result;

        if (root.TryGetString("html") is { Length: > 0 } html)
            return html;

        if (root.TryGetString("content") is { Length: > 0 } content)
            return content;

        if (root.TryGetString("text") is { Length: > 0 } text)
            return text;

        if (root.TryGetProperty("interrupt", out var interrupt)
            && interrupt.ValueKind == JsonValueKind.Object
            && interrupt.TryGetString("message") is { Length: > 0 } interruptMessage)
            return interruptMessage;

        return root.TryGetString("title") ?? string.Empty;
    }

    private static string ExtractSambaNovaAgentPrompt(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var selectedItem = (request.Input?.Items ?? [])
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(ExtractSambaNovaAgentText(item.Content)))
            ?? (request.Input?.Items ?? [])
                .LastOrDefault(item => !string.IsNullOrWhiteSpace(ExtractSambaNovaAgentText(item.Content)));

        if (selectedItem is not null)
            return ExtractSambaNovaAgentText(selectedItem.Content);

        return request.Instructions ?? string.Empty;
    }

    private static string ExtractSambaNovaAgentCode(AIRequest request)
        => string.Join("\n", (request.Input?.Items ?? [])
            .SelectMany(item => item.Content ?? [])
            .OfType<AIFileContentPart>()
            .Select(part => ExtractSambaNovaFileText(part.Data))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string ExtractSambaNovaAgentText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .Select(part => part switch
            {
                AITextContentPart text => text.Text,
                AIReasoningContentPart reasoning => reasoning.Text,
                _ => null
            })
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static IEnumerable<SambaNovaAgentFile> ExtractSambaNovaAgentFiles(AIRequest request)
    {
        var index = 0;
        foreach (var part in (request.Input?.Items ?? []).SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>())
        {
            index++;
            var bytes = ExtractSambaNovaFileBytes(part.Data);
            if (bytes.Length == 0)
                continue;

            var mediaType = string.IsNullOrWhiteSpace(part.MediaType) ? MediaTypeNames.Application.Octet : part.MediaType!;
            var fileName = string.IsNullOrWhiteSpace(part.Filename)
                ? $"file-{index}{GuessSambaNovaAgentFileExtension(mediaType)}"
                : part.Filename!;

            yield return new SambaNovaAgentFile(fileName, mediaType, bytes);
        }
    }

    private static byte[] ExtractSambaNovaFileBytes(object? data)
    {
        switch (data)
        {
            case null:
                return [];
            case byte[] bytes:
                return bytes;
            case string text:
                return DecodeSambaNovaFileString(text);
            case JsonElement json when json.ValueKind == JsonValueKind.String:
                return DecodeSambaNovaFileString(json.GetString() ?? string.Empty);
            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                foreach (var propertyName in new[] { "data", "content", "base64", "b64_json" })
                {
                    if (json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                        return DecodeSambaNovaFileString(value.GetString() ?? string.Empty);
                }

                return Encoding.UTF8.GetBytes(json.GetRawText());
            default:
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, SambaNovaAgentJson));
        }
    }

    private static string? ExtractSambaNovaFileText(object? data)
    {
        var bytes = ExtractSambaNovaFileBytes(data);
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[] DecodeSambaNovaFileString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return [];

        var data = value;
        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            data = value[(commaIndex + 1)..];

        return TryDecodeBase64(data, out var bytes)
            ? bytes
            : Encoding.UTF8.GetBytes(value);
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string GuessSambaNovaAgentFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "text/csv" => ".csv",
            "application/json" => ".json",
            "text/html" => ".html",
            "text/plain" => ".txt",
            "application/pdf" => ".pdf",
            _ => string.Empty
        };

    private static JsonElement? ExtractSambaNovaProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("sambanova", out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, SambaNovaAgentJson),
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToElement(dictionary, SambaNovaAgentJson),
            _ => JsonSerializer.SerializeToElement(raw, SambaNovaAgentJson)
        };

        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static JsonObject JsonElementObjectToJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];
    }

    private static bool ContainsAnySambaNovaAgentProperty(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        return element.EnumerateObject().Any(property => propertyNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsAnySambaNovaAgentMetadataKey(Dictionary<string, object?> metadata, params string[] propertyNames)
        => metadata.Keys.Any(key => propertyNames.Any(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase)));

    private static bool HasSambaNovaJsonProperty(JsonObject obj, string propertyName)
        => obj.Any(property => string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetSambaNovaJsonNode(JsonObject obj, string propertyName, out JsonNode? node)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                node = property.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static string? TryGetSambaNovaJsonString(JsonObject obj, string propertyName)
    {
        if (!TryGetSambaNovaJsonNode(obj, propertyName, out var node) || node is null)
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;

        return null;
    }

    private static string? ConvertSambaNovaAgentFormValue(JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
                return text;
            if (value.TryGetValue<bool>(out var boolean))
                return boolean.ToString().ToLowerInvariant();
            if (value.TryGetValue<int>(out var integer))
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var longValue))
                return longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var doubleValue))
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return node.ToJsonString(SambaNovaAgentJson);
    }

    private static JsonElement? CloneSambaNovaAgentProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;
}
