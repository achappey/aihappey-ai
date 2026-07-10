using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private const int DefaultInfronMediaPollIntervalSeconds = 2;
    private const int DefaultInfronMediaPollTimeoutMinutes = 10;

    private sealed record InfronMediaTaskResult(
        string? TaskId,
        string? Object,
        string Status,
        string Raw,
        JsonElement Root);

    private async Task<InfronMediaTaskResult> WaitForInfronMediaTaskAsync(
        string mediaKind,
        JsonElement createRoot,
        JsonElement? metadata,
        CancellationToken cancellationToken)
    {
        var createResult = NormalizeInfronMediaTaskResult(createRoot, null);

        if (IsInfronMediaTerminalStatus(createResult.Status) || string.IsNullOrWhiteSpace(createResult.TaskId))
            return createResult;

        return await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollInfronMediaTaskAsync(mediaKind, createResult.TaskId!, metadata, ct),
            isTerminal: result => IsInfronMediaTerminalStatus(result.Status),
            interval: TimeSpan.FromSeconds(Math.Max(1, ResolveInfronMediaPollIntervalSeconds(metadata))),
            timeout: TimeSpan.FromMinutes(Math.Max(1, ResolveInfronMediaPollTimeoutMinutes(metadata))),
            maxAttempts: ResolveInfronMediaPollMaxAttempts(metadata),
            cancellationToken: cancellationToken);
    }

    private async Task<InfronMediaTaskResult> PollInfronMediaTaskAsync(
        string mediaKind,
        string taskId,
        JsonElement? metadata,
        CancellationToken cancellationToken)
    {
        var pollUri = ResolveInfronMediaTaskUri(mediaKind, taskId, metadata);
        using var request = new HttpRequestMessage(HttpMethod.Get, pollUri);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Infron {mediaKind} poll failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return NormalizeInfronMediaTaskResult(document.RootElement.Clone(), taskId, raw);
    }

    private static InfronMediaTaskResult NormalizeInfronMediaTaskResult(
        JsonElement root,
        string? fallbackTaskId,
        string? raw = null)
    {
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : root;

        var taskId = data.TryGetString("task_id")
            ?? data.TryGetString("taskId")
            ?? data.TryGetString("id")
            ?? root.TryGetString("task_id")
            ?? root.TryGetString("taskId")
            ?? root.TryGetString("id")
            ?? fallbackTaskId;

        var status = data.TryGetString("status")
            ?? root.TryGetString("status")
            ?? (HasInfronMediaOutputs(root) ? "completed" : "created");

        var obj = data.TryGetString("object") ?? root.TryGetString("object");

        return new InfronMediaTaskResult(taskId, obj, status, raw ?? root.GetRawText(), root);
    }

    private static Uri ResolveInfronMediaTaskUri(string mediaKind, string taskId, JsonElement? metadata)
    {
        var pollUrl = ReadInfronMediaString(metadata, "poll_url", "pollUrl", "task_url", "taskUrl");
        if (!string.IsNullOrWhiteSpace(pollUrl))
            return new Uri(pollUrl.Replace("{task_id}", Uri.EscapeDataString(taskId), StringComparison.OrdinalIgnoreCase));

        return new Uri($"https://media.onerouter.pro/v1/{mediaKind}/tasks/{Uri.EscapeDataString(taskId)}");
    }

    private static IEnumerable<JsonElement> EnumerateInfronMediaOutputItems(JsonElement root)
    {
        if (root.TryGetProperty("data", out var dataElement))
        {
            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElement.EnumerateArray())
                    yield return item;
            }
            else if (dataElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in EnumerateInfronMediaOutputItemsFromObject(dataElement))
                    yield return item;
            }
        }

        foreach (var item in EnumerateInfronMediaOutputItemsFromObject(root))
            yield return item;
    }

    private static IEnumerable<JsonElement> EnumerateInfronMediaOutputItemsFromObject(JsonElement element)
    {
        foreach (var name in new[] { "outputs", "output", "urls", "files" })
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    yield return item;
            }
            else if (value.ValueKind is JsonValueKind.String or JsonValueKind.Object)
            {
                yield return value;
            }
        }
    }

    private static bool HasInfronMediaOutputs(JsonElement root)
        => EnumerateInfronMediaOutputItems(root).Any();

    private static string? TryGetInfronMediaUrl(JsonElement item, params string[] names)
    {
        if (item.ValueKind == JsonValueKind.String)
            return item.GetString();

        if (item.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            var value = item.TryGetString(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return item.TryGetString("url")
            ?? item.TryGetString("signed_url")
            ?? item.TryGetString("signedUrl")
            ?? item.TryGetString("download_url")
            ?? item.TryGetString("downloadUrl");
    }

    private static bool IsInfronMediaTerminalStatus(string? status)
        => IsInfronMediaSuccessStatus(status)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    private static bool IsInfronMediaSuccessStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

    private static string GetInfronMediaError(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : root;

        return data.TryGetString("fail_reason")
            ?? data.TryGetString("failReason")
            ?? data.TryGetString("error")
            ?? data.TryGetString("message")
            ?? root.TryGetString("message")
            ?? "Unknown error";
    }

    private static int ResolveInfronMediaPollIntervalSeconds(JsonElement? metadata)
        => TryReadInfronMediaInt(metadata, "poll_interval_seconds", "pollIntervalSeconds") ?? DefaultInfronMediaPollIntervalSeconds;

    private static int ResolveInfronMediaPollTimeoutMinutes(JsonElement? metadata)
        => TryReadInfronMediaInt(metadata, "poll_timeout_minutes", "pollTimeoutMinutes") ?? DefaultInfronMediaPollTimeoutMinutes;

    private static int? ResolveInfronMediaPollMaxAttempts(JsonElement? metadata)
        => TryReadInfronMediaInt(metadata, "poll_max_attempts", "pollMaxAttempts");

    private static int? TryReadInfronMediaInt(JsonElement? metadata, params string[] names)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } element)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static string? ReadInfronMediaString(JsonElement? metadata, params string[] names)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } element)
            return null;

        foreach (var name in names)
        {
            var value = element.TryGetString(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool IsInfronMediaControlOption(string name)
        => string.Equals(name, "poll_url", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollUrl", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "task_url", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "taskUrl", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_interval_seconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollIntervalSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_timeout_minutes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollTimeoutMinutes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_max_attempts", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollMaxAttempts", StringComparison.OrdinalIgnoreCase);

    private async Task<(string Base64, string MediaType, long ContentLength)> DownloadInfronMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Infron media download failed ({(int)response.StatusCode}): {error}");
        }

        return (
            Convert.ToBase64String(bytes),
            response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType,
            bytes.LongLength);
    }
}
