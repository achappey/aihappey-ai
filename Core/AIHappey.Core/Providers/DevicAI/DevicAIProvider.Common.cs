using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    private const string CreateThreadToolName = "create_devicai_thread";
    private const string CreateChatToolName = "create_devicai_chat";
    private const string ApproveThreadToolName = "approve_devicai_thread";
    private static readonly JsonSerializerOptions DevicAIJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> TerminalChatStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed", "error", "limit_exceeded"
    };

    private enum DevicAIRouteKind
    {
        Agent,
        Assistant,
        Whisper
    }

    private sealed record DevicAIRoute(DevicAIRouteKind Kind, string Target)
    {
        public string ModelId => Kind switch
        {
            DevicAIRouteKind.Agent => $"devicai/agents/{Target}",
            DevicAIRouteKind.Assistant => $"devicai/assistants/{Target}",
            _ => "devicai/whisper"
        };
    }

    private sealed record DevicAIAgentExecution(
        DevicAIRoute Route,
        JsonElement Thread,
        bool Created,
        int BaselineMessageCount,
        JsonElement CommandResponse);

    private sealed record DevicAIAssistantExecution(
        DevicAIRoute Route,
        string ChatUid,
        bool Created,
        IReadOnlyList<JsonElement> Messages,
        JsonElement Raw,
        JsonElement? History,
        string? Status,
        IReadOnlyList<JsonElement> Uploads);

    private sealed record DevicAIUploadedFile(
        string Name,
        string DownloadUrl,
        string FileType,
        JsonElement Raw,
        bool IsImage);

    private sealed record DevicAIAttachments(
        IReadOnlyList<object> Files,
        IReadOnlyList<object> Images,
        IReadOnlyList<JsonElement> Uploads);

    private static DevicAIRoute ParseDevicAIRoute(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("DevicAI requires 'devicai/agents/{agentId}', 'devicai/assistants/{identifier}', or 'devicai/whisper'.", nameof(model));

        var value = model.Trim().Trim('/');
        if (value.StartsWith("devicai/", StringComparison.OrdinalIgnoreCase))
            value = value[8..];

        if (string.Equals(value, "whisper", StringComparison.OrdinalIgnoreCase))
            return new DevicAIRoute(DevicAIRouteKind.Whisper, "whisper");

        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
            throw new ArgumentException($"Invalid DevicAI model '{model}'.", nameof(model));

        var kind = value[..slash];
        var target = value[(slash + 1)..];
        return kind.ToLowerInvariant() switch
        {
            "agents" => new DevicAIRoute(DevicAIRouteKind.Agent, target),
            "assistants" => new DevicAIRoute(DevicAIRouteKind.Assistant, target),
            _ => throw new ArgumentException($"Invalid DevicAI model '{model}'.", nameof(model))
        };
    }

    private static string ExtractLatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = JoinText(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        throw new InvalidOperationException("DevicAI requires a non-empty latest user message.");
    }

    private static string JoinText(IEnumerable<AIContentPart>? content)
        => string.Join("\n", content?.OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

    private static JsonElement? GetDevicAIOptions(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue("devicai", out var raw)
            || raw is null)
            return null;

        var json = raw is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(raw, DevicAIJson);
        return json.ValueKind == JsonValueKind.Object ? json.Clone() : null;
    }

    private static void CopyOption(
        JsonElement? options,
        IDictionary<string, object?> payload,
        string destination,
        params string[] aliases)
    {
        if (!options.HasValue)
            return;

        foreach (var name in new[] { destination }.Concat(aliases))
        {
            if (!TryGetProperty(options.Value, name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            payload[destination] = value.Clone();
            return;
        }
    }

    private static TimeSpan ResolvePollInterval(AIRequest request)
    {
        var value = GetOptionInt(request, "pollIntervalMs", "poll_interval_ms");
        return value is > 0 ? TimeSpan.FromMilliseconds(value.Value) : DefaultPollInterval;
    }

    private static TimeSpan ResolvePollTimeout(AIRequest request)
    {
        var value = GetOptionInt(request, "pollTimeoutMs", "poll_timeout_ms");
        return value is > 0 ? TimeSpan.FromMilliseconds(value.Value) : DefaultPollTimeout;
    }

    private static int? GetOptionInt(AIRequest request, params string[] names)
    {
        var options = GetDevicAIOptions(request);
        if (!options.HasValue)
            return null;
        foreach (var name in names)
            if (TryGetProperty(options.Value, name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
                return parsed;
        return null;
    }

    private static bool TryFindThreadId(AIRequest request, out string threadId)
        => TryFindIdentity(request, ["threadId", "thread_id"], out threadId);

    private static bool TryFindChatUid(AIRequest request, out string chatUid)
        => TryFindIdentity(request, ["chatUid", "chatUID", "chat_uid"], out chatUid);

    private static bool TryFindIdentity(AIRequest request, string[] names, out string identity)
    {
        var options = GetDevicAIOptions(request);
        identity = options.HasValue ? GetString(options.Value, names) ?? string.Empty : string.Empty;
        if (!string.IsNullOrWhiteSpace(identity))
            return true;

        identity = GetDictionaryString(request.Input?.Metadata, names) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(identity))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractIdentity(item.Metadata, names, out identity))
                return true;

            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractIdentity(tool.Output, names, out identity)
                    || TryExtractIdentity(tool.Metadata, names, out identity)
                    || TryExtractIdentity(tool.Input, names, out identity))
                    return true;
            }
        }

        identity = string.Empty;
        return false;
    }

    private static bool TryExtractIdentity(object? raw, string[] names, out string identity)
    {
        identity = string.Empty;
        if (raw is null)
            return false;

        var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw, DevicAIJson);
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var nestedName in new[] { "structuredContent", "output", "devicai", "thread", "chat", "raw" })
            if (TryGetProperty(element, nestedName, out var nested)
                && TryExtractIdentity(nested, names, out identity))
                return true;

        identity = GetString(element, names) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(identity);
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        object? payload,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, DevicAIJson), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"DevicAI {operation} failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}",
                null,
                response.StatusCode);

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, DevicAIJson);
        return JsonSerializer.Deserialize<JsonElement>(body, DevicAIJson).Clone();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        ApplyAuthHeader(request);
        return request;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
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

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static long? GetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var parsed))
                return parsed;
        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
                return parsed;
        return null;
    }

    private static decimal? GetDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out var parsed))
                return parsed;
        return null;
    }

    private static bool? GetBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        return null;
    }

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Select(item => item.Clone()).ToList();
        return [];
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

    private static DateTimeOffset? GetDate(JsonElement element, params string[] names)
        => DateTimeOffset.TryParse(GetString(element, names), out var parsed) ? parsed : null;

    private static Dictionary<string, Dictionary<string, object>> ScopedMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai"] = new Dictionary<string, object> { ["raw"] = raw.Clone() }
        };

    private static Dictionary<string, object> LooseMetadata(JsonElement raw)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai"] = new Dictionary<string, object> { ["raw"] = raw.Clone() }
        };

    private static int? AddTokens(int? input, int? output)
        => input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null;
}
