using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.ShadowOS;

public partial class ShadowOSProvider
{
    private const string SessionModel = "agent";
    private const string AccountModel = "agent-account";
    private const string SessionToolName = "create_shadowos_session";
    private static readonly JsonSerializerOptions ShadowOSJson = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        var execution = await ExecuteShadowOSAsync(request, cancellationToken);
        var content = new List<AIContentPart>();

        if (execution.CreatedSession)
            content.Add(CreateSessionToolPart(execution));

        if (!string.IsNullOrWhiteSpace(execution.Answer))
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = execution.Answer,
                Metadata = CreateResponseMetadata(execution)
            });
        }

        foreach (var file in execution.Files)
        {
            content.Add(new AIFileContentPart
            {
                Type = "file",
                Filename = file.Name,
                MediaType = file.MediaType,
                Data = file.Base64,
                Metadata = CreateFileMetadata(file)
            });
        }

        var metadata = CreateResponseMetadata(execution);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = execution.Model.ToModelId(GetIdentifier()),
            Status = "completed",
            Output = content.Count == 0
                ? null
                : new AIOutput
                {
                    Items =
                    [
                        new AIOutputItem
                        {
                            Role = "assistant",
                            Content = content,
                            Metadata = metadata
                        }
                    ],
                    Metadata = metadata
                },
            Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var execution = await ExecuteShadowOSAsync(request, cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = CreateResponseMetadata(execution);

        if (execution.CreatedSession)
        {
            var toolCallId = BuildSessionToolCallId(execution.SessionId);
            var providerMetadata = CreateScopedMetadata(execution);
            yield return CreateEvent("tool-input-available", toolCallId,
                new AIToolInputAvailableEventData
                {
                    ToolName = SessionToolName,
                    Title = "Create Shadow-OS session",
                    Input = new { scope = execution.Scope },
                    ProviderExecuted = true,
                    ProviderMetadata = providerMetadata
                }, timestamp, metadata);
            yield return CreateEvent("tool-output-available", toolCallId,
                new AIToolOutputAvailableEventData
                {
                    ToolName = SessionToolName,
                    Output = CreateSessionToolResult(execution),
                    ProviderExecuted = true,
                    ProviderMetadata = providerMetadata
                }, timestamp, metadata);
        }

        if (!string.IsNullOrWhiteSpace(execution.Answer))
        {
            var textId = $"shadowos-text-{execution.RequestId ?? Guid.NewGuid().ToString("N")}";
            var textMetadata = CreateFlatProviderMetadata(execution);
            yield return CreateEvent("text-start", textId,
                new AITextStartEventData { ProviderMetadata = textMetadata }, timestamp, metadata);
            yield return CreateEvent("text-delta", textId,
                new AITextDeltaEventData { Delta = execution.Answer, ProviderMetadata = textMetadata },
                timestamp, metadata);
            yield return CreateEvent("text-end", textId,
                new AITextEndEventData { ProviderMetadata = textMetadata }, timestamp, metadata);
        }

        foreach (var file in execution.Files)
        {
            yield return CreateEvent("file", $"shadowos-file-{Guid.NewGuid():N}",
                new AIFileEventData
                {
                    Filename = file.Name,
                    MediaType = file.MediaType,
                    Url = $"data:{file.MediaType};base64,{file.Base64}",
                    ProviderMetadata = CreateScopedFileMetadata(file)
                }, timestamp, CreateFileMetadata(file));
        }

        yield return CreateEvent("finish", execution.RequestId ?? execution.SessionId,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = execution.Model.ToModelId(GetIdentifier()),
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    execution.Model.ToModelId(GetIdentifier()),
                    timestamp,
                    execution.Usage,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        [GetIdentifier()] = new
                        {
                            session_id = execution.SessionId,
                            request_id = execution.RequestId,
                            scope = execution.Scope,
                            usage = execution.Usage
                        }
                    })
            }, timestamp, metadata);
    }

    private async Task<ShadowOSExecution> ExecuteShadowOSAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeModel(request.Model);
        var scope = model == AccountModel ? "account" : "session";
        var createdSession = !TryFindSessionId(request, out var sessionId);
        if (createdSession)
            sessionId = Guid.NewGuid().ToString("N");

        var payload = new Dictionary<string, object?>
        {
            ["input"] = ExtractLatestUserText(request),
            ["session_id"] = sessionId,
            ["scope"] = scope
        };

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/agent")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ShadowOSJson), Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var rawText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadErrorDetail(rawText);
            throw new HttpRequestException(
                $"Shadow-OS agent request failed with status {(int)response.StatusCode}: {detail}",
                null, response.StatusCode);
        }

        JsonElement raw;
        try
        {
            raw = JsonSerializer.Deserialize<JsonElement>(rawText, ShadowOSJson).Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Shadow-OS returned an invalid JSON response.", exception);
        }

        var returnedSessionId = GetString(raw, "session_id");
        if (string.IsNullOrWhiteSpace(returnedSessionId))
            throw new InvalidOperationException("Shadow-OS response did not include session_id.");

        var files = await DownloadFilesAsync(ReadFiles(raw), cancellationToken);
        return new ShadowOSExecution(
            model,
            scope,
            returnedSessionId,
            createdSession,
            GetString(raw, "answer") ?? string.Empty,
            GetString(raw, "request_id"),
            files,
            TryGetProperty(raw, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? usage.Clone()
                : null,
            raw);
    }

    private async Task<List<ShadowOSFile>> DownloadFilesAsync(
        IReadOnlyList<ShadowOSFileReference> files,
        CancellationToken cancellationToken)
    {
        var downloaded = new List<ShadowOSFile>(files.Count);
        foreach (var file in files)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Shadow-OS file download failed for '{file.Name}' with status {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            if (bytes.Length == 0)
                throw new InvalidOperationException($"Shadow-OS file download returned an empty file for '{file.Name}'.");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType))
                mediaType = string.IsNullOrWhiteSpace(file.Mime)
                    ? MediaTypeNames.Application.Octet
                    : file.Mime;

            downloaded.Add(new ShadowOSFile(
                file.Name,
                file.DownloadUrl,
                mediaType,
                Convert.ToBase64String(bytes),
                file.Raw));
        }

        return downloaded;
    }

    private static string NormalizeModel(string? model)
    {
        var value = (model ?? string.Empty).Trim().Trim('/');
        if (value.StartsWith("shadowos/", StringComparison.OrdinalIgnoreCase))
            value = value[9..];

        if (string.Equals(value, SessionModel, StringComparison.OrdinalIgnoreCase))
            return SessionModel;
        if (string.Equals(value, AccountModel, StringComparison.OrdinalIgnoreCase))
            return AccountModel;

        throw new ArgumentException(
            "Shadow-OS requires model 'shadowos/agent' or 'shadowos/agent-account'.", nameof(model));
    }

    private static string ExtractLatestUserText(AIRequest request)
    {
        var latestUser = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = string.Join("\n", latestUser?.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
        if (string.IsNullOrWhiteSpace(text))
            text = request.Input?.Text ?? request.Instructions;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Shadow-OS requires a non-empty latest user message.");
        return text.Trim();
    }

    private bool TryFindSessionId(AIRequest request, out string sessionId)
    {
        sessionId = GetDictionaryString(GetProviderOptions(request), "session_id")
                    ?? GetDictionaryString(GetProviderOptions(request), "sessionId")
                    ?? GetDictionaryString(request.Input?.Metadata, "session_id")
                    ?? GetDictionaryString(request.Input?.Metadata, "sessionId")
                    ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractSessionId(item.Metadata, out sessionId))
                return true;

            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractSessionId(tool.Output, out sessionId)
                    || TryExtractSessionId(tool.Metadata, out sessionId)
                    || (string.Equals(tool.ToolName, SessionToolName, StringComparison.OrdinalIgnoreCase)
                        && TryExtractSessionId(tool.Input, out sessionId)))
                    return true;
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private static Dictionary<string, object?>? GetProviderOptions(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue("shadowos", out var value)
            || value is null)
            return null;

        var element = ToJsonElement(value);
        return element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
            : null;
    }

    private static bool TryExtractSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;

        var element = ToJsonElement(value);
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var nestedName in new[] { "structuredContent", "output", "shadowos", "session" })
        {
            if (TryGetProperty(element, nestedName, out var nested)
                && TryExtractSessionId(nested, out sessionId))
                return true;
        }

        sessionId = GetString(element, "session_id") ?? GetString(element, "sessionId") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private AIToolCallContentPart CreateSessionToolPart(ShadowOSExecution execution)
        => new()
        {
            Type = "tool-call",
            ToolCallId = BuildSessionToolCallId(execution.SessionId),
            ToolName = SessionToolName,
            Title = "Create Shadow-OS session",
            Input = new { scope = execution.Scope },
            Output = CreateSessionToolResult(execution),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateSessionMetadata(execution)
        };

    private static CallToolResult CreateSessionToolResult(ShadowOSExecution execution)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = SessionToolName,
                sessionId = execution.SessionId,
                session_id = execution.SessionId,
                scope = execution.Scope,
                request_id = execution.RequestId
            }, ShadowOSJson)
        };

    private Dictionary<string, object?> CreateSessionMetadata(ShadowOSExecution execution)
        => new()
        {
            [GetIdentifier()] = new
            {
                type = SessionToolName,
                sessionId = execution.SessionId,
                session_id = execution.SessionId,
                scope = execution.Scope,
                request_id = execution.RequestId
            },
            ["type"] = SessionToolName,
            ["tool_name"] = SessionToolName,
            ["sessionId"] = execution.SessionId,
            ["session_id"] = execution.SessionId
        };

    private Dictionary<string, object?> CreateResponseMetadata(ShadowOSExecution execution)
        => new()
        {
            ["shadowos.session_id"] = execution.SessionId,
            ["shadowos.request_id"] = execution.RequestId,
            ["shadowos.scope"] = execution.Scope,
            ["shadowos.usage"] = execution.Usage,
            ["shadowos.download_url"] = execution.Files.FirstOrDefault()?.DownloadUrl,
            ["shadowos.raw"] = execution.Raw.Clone()
        };

    private static Dictionary<string, object?> CreateFileMetadata(ShadowOSFile file)
        => new()
        {
            ["shadowos.file.name"] = file.Name,
            ["shadowos.file.mime"] = file.MediaType,
            ["shadowos.file.download_url"] = file.DownloadUrl,
            ["shadowos.file.raw"] = file.Raw.Clone()
        };

    private static Dictionary<string, Dictionary<string, object>> CreateScopedMetadata(
        ShadowOSExecution execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["shadowos"] = new Dictionary<string, object>
            {
                ["session_id"] = execution.SessionId,
                ["request_id"] = execution.RequestId ?? string.Empty,
                ["scope"] = execution.Scope
            }
        };

    private static Dictionary<string, object> CreateFlatProviderMetadata(ShadowOSExecution execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["shadowos"] = new Dictionary<string, object>
            {
                ["session_id"] = execution.SessionId,
                ["request_id"] = execution.RequestId ?? string.Empty,
                ["scope"] = execution.Scope
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateScopedFileMetadata(ShadowOSFile file)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["shadowos"] = new Dictionary<string, object>
            {
                ["name"] = file.Name,
                ["mime"] = file.MediaType,
                ["download_url"] = file.DownloadUrl
            }
        };

    private AIStreamEvent CreateEvent(string type, string? id, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            },
            Metadata = metadata
        };

    private static List<ShadowOSFileReference> ReadFiles(JsonElement root)
    {
        if (!TryGetProperty(root, "files", out var files) || files.ValueKind != JsonValueKind.Array)
            return [];

        return files.EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.Object)
            .Select(file => new ShadowOSFileReference(
                GetString(file, "name") ?? "download",
                GetString(file, "download_url") ?? string.Empty,
                GetString(file, "mime"),
                file.Clone()))
            .Where(file => !string.IsNullOrWhiteSpace(file.DownloadUrl))
            .ToList();
    }

    private static string TryReadErrorDetail(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Empty error response.";
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(raw, ShadowOSJson);
            return GetString(element, "detail") ?? GetString(element, "error") ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static JsonElement ToJsonElement(object value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, ShadowOSJson);

    private static string? GetDictionaryString(Dictionary<string, object?>? values, string name)
    {
        if (values is null || !values.TryGetValue(name, out var value) || value is null)
            return null;
        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value.ToString();
    }

    private static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    private static string BuildSessionToolCallId(string sessionId)
        => $"shadowos-create-session-{sessionId}";

    private sealed record ShadowOSFileReference(string Name, string DownloadUrl, string? Mime, JsonElement Raw);

    private sealed record ShadowOSFile(
        string Name,
        string DownloadUrl,
        string MediaType,
        string Base64,
        JsonElement Raw);

    private sealed record ShadowOSExecution(
        string Model,
        string Scope,
        string SessionId,
        bool CreatedSession,
        string Answer,
        string? RequestId,
        List<ShadowOSFile> Files,
        JsonElement? Usage,
        JsonElement Raw);
}
