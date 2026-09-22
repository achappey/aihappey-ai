using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AgentContainer;

public partial class AgentContainerProvider
{
    private static readonly JsonSerializerOptions AgentContainerJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "finished", "failed", "cancelled", "expired"
    };

    private sealed record AgentContainerRoute(string AgentId, string Mode)
    {
        public bool IsConversation => string.Equals(Mode, "conversation", StringComparison.OrdinalIgnoreCase);
        public string ModelId => $"agentcontainer/{AgentId}/{Mode}";
    }

    private sealed record AgentContainerUploadedFile(string Id, string Filename, string? ContentType, JsonElement Raw);

    private sealed record AgentContainerExecution(
        AgentContainerRoute Route,
        JsonElement Task,
        bool Created,
        int? TurnNumber,
        IReadOnlyList<JsonElement> Messages,
        IReadOnlyList<JsonElement> Activities,
        IReadOnlyList<AgentContainerDownloadedFile> Files,
        JsonElement? CommandResponse);

    private sealed record AgentContainerDownloadedFile(
        string Id,
        string Filename,
        string MediaType,
        byte[] Bytes,
        JsonElement Raw);

    private static AgentContainerRoute ParseAgentContainerRoute(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("AgentContainer requires 'agentcontainer/{agentId}/conversation' or 'agentcontainer/{agentId}/task'.", nameof(model));

        var value = model.Trim().Trim('/');
        if (value.StartsWith("agentcontainer/", StringComparison.OrdinalIgnoreCase))
            value = value[15..];

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 || string.IsNullOrWhiteSpace(segments[0])
            || segments[1] is not ("conversation" or "task"))
            throw new ArgumentException($"Invalid AgentContainer model '{model}'. Expected 'agentcontainer/{{agentId}}/conversation' or 'agentcontainer/{{agentId}}/task'.", nameof(model));

        return new AgentContainerRoute(segments[0], segments[1]);
    }

    private static string ExtractLatestAgentContainerUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        throw new InvalidOperationException("AgentContainer requires a non-empty latest user message.");
    }

    private static JsonElement? GetAgentContainerOptions(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue("agentcontainer", out var raw)
            || raw is null)
            return null;

        var element = raw is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(raw, AgentContainerJson);
        return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
    }

    private static Dictionary<string, object?> BuildAgentContainerPayload(
        AIRequest request,
        IEnumerable<string> allowedKeys)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var options = GetAgentContainerOptions(request);
        if (!options.HasValue)
            return payload;

        var allowed = allowedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.Value.EnumerateObject())
        {
            if (allowed.Contains(property.Name))
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static void MergeInputFiles(Dictionary<string, object?> payload, IReadOnlyList<AgentContainerUploadedFile> uploaded)
    {
        var files = new List<object>();
        if (payload.TryGetValue("inputFiles", out var configured) && configured is JsonElement array
            && array.ValueKind == JsonValueKind.Array)
            files.AddRange(array.EnumerateArray().Select(item => (object)item.Clone()));

        files.AddRange(uploaded.Select(file => (object)new { fileId = file.Id, snapshot = "include" }));
        if (files.Count > 0)
            payload["inputFiles"] = files;
        else
            payload.Remove("inputFiles");
    }

    private static string ResolveAgentContainerIdempotencyKey(AIRequest request, string operation, string prompt, string? taskId)
    {
        var options = GetAgentContainerOptions(request);
        var configured = options.HasValue
            ? GetAgentContainerString(options.Value, "idempotencyKey")
              ?? GetAgentContainerString(options.Value, "idempotency_key")
            : null;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        if (!string.IsNullOrWhiteSpace(request.Id))
            return request.Id!;

        var seed = $"{operation}\n{request.Model}\n{taskId}\n{prompt}\n{Guid.NewGuid():N}";
        return "aihappey-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..32];
    }

    private static TimeSpan ResolveAgentContainerPollInterval(AIRequest request)
    {
        var milliseconds = GetAgentContainerOptionInt(request, "pollIntervalMs", "poll_interval_ms");
        return milliseconds is > 0 ? TimeSpan.FromMilliseconds(milliseconds.Value) : DefaultPollInterval;
    }

    private static TimeSpan ResolveAgentContainerPollTimeout(AIRequest request)
    {
        var milliseconds = GetAgentContainerOptionInt(request, "pollTimeoutMs", "poll_timeout_ms");
        return milliseconds is > 0 ? TimeSpan.FromMilliseconds(milliseconds.Value) : DefaultPollTimeout;
    }

    private static int? GetAgentContainerOptionInt(AIRequest request, params string[] names)
    {
        var options = GetAgentContainerOptions(request);
        if (!options.HasValue)
            return null;
        foreach (var name in names)
            if (TryGetAgentContainerProperty(options.Value, name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                return parsed;
        return null;
    }

    private static bool TryFindAgentContainerTaskId(AIRequest request, out string taskId)
    {
        var options = GetAgentContainerOptions(request);
        taskId = options.HasValue
            ? GetAgentContainerString(options.Value, "taskId")
              ?? GetAgentContainerString(options.Value, "task_id")
              ?? GetAgentContainerString(options.Value, "conversationId")
              ?? GetAgentContainerString(options.Value, "conversation_id")
              ?? string.Empty
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(taskId))
            return true;

        taskId = GetDictionaryString(request.Input?.Metadata, "taskId", "task_id", "conversationId", "conversation_id") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(taskId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractAgentContainerTaskId(item.Metadata, out taskId))
                return true;

            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractAgentContainerTaskId(tool.Output, out taskId)
                    || TryExtractAgentContainerTaskId(tool.Metadata, out taskId)
                    || TryExtractAgentContainerTaskId(tool.Input, out taskId))
                    return true;
            }
        }

        taskId = string.Empty;
        return false;
    }

    private static bool TryExtractAgentContainerTaskId(object? raw, out string taskId)
    {
        taskId = string.Empty;
        if (raw is null)
            return false;
        var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw, AgentContainerJson);
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var nestedName in new[] { "structuredContent", "output", "agentcontainer", "task", "conversation" })
            if (TryGetAgentContainerProperty(element, nestedName, out var nested)
                && TryExtractAgentContainerTaskId(nested, out taskId))
                return true;

        taskId = GetAgentContainerString(element, "taskId")
                 ?? GetAgentContainerString(element, "task_id")
                 ?? GetAgentContainerString(element, "conversationId")
                 ?? GetAgentContainerString(element, "conversation_id")
                 ?? string.Empty;
        return !string.IsNullOrWhiteSpace(taskId);
    }

    private static string? GetDictionaryString(Dictionary<string, object?>? dictionary, params string[] names)
    {
        if (dictionary is null)
            return null;
        foreach (var name in names)
            if (dictionary.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                return value!.ToString();
        return null;
    }

    private async Task<JsonElement> SendAgentContainerJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        string operation,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        ApplyAuthHeader(request);
        request.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, AgentContainerJson), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AgentContainer {operation} failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, AgentContainerJson);
        return JsonSerializer.Deserialize<JsonElement>(body, AgentContainerJson).Clone();
    }

    private async Task<byte[]> SendAgentContainerBytesAsync(string path, string operation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApplyAuthHeader(request);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"AgentContainer {operation} failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static bool TryGetAgentContainerProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        return false;
    }

    private static string? GetAgentContainerString(JsonElement element, string name)
        => TryGetAgentContainerProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetAgentContainerInt(JsonElement element, string name)
        => TryGetAgentContainerProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : null;

    private static decimal? GetAgentContainerDecimal(JsonElement element, string name)
        => TryGetAgentContainerProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var parsed) ? parsed : null;

    private static bool? GetAgentContainerBoolean(JsonElement element, string name)
        => TryGetAgentContainerProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? GetAgentContainerDate(JsonElement element, string name)
        => DateTimeOffset.TryParse(GetAgentContainerString(element, name), out var parsed) ? parsed : null;

    private static bool IsTerminalAgentContainerStatus(string? status)
        => status is not null && TerminalStatuses.Contains(status);
}
