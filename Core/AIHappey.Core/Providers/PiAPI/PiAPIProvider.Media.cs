using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.PiAPI;

public partial class PiAPIProvider
{
    private const string PiApiTaskEndpoint = "api/v1/task";

    private static readonly JsonSerializerOptions PiApiMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record PiApiTaskResult(string? TaskId, string? Status, JsonElement Root);

    private async Task<PiApiTaskResult> CreateMediaTaskAsync(
        string model,
        string defaultTaskType,
        Dictionary<string, object?> input,
        Dictionary<string, JsonElement>? providerOptions,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var options = GetPiApiOptions(providerOptions);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ToPiApiModelId(model),
            ["task_type"] = GetStringOption(options, "task_type") ?? defaultTaskType,
            ["input"] = input
        };

        MergeJsonObject(input, GetObjectOption(options, "input"));

        var config = GetObjectOption(options, "config");
        if (config is not null)
            payload["config"] = config;

        using var request = new HttpRequestMessage(HttpMethod.Post, PiApiTaskEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, PiApiMediaJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PiAPI task creation failed ({(int)response.StatusCode}): {raw}");

        var task = ParseTaskResult(raw);
        ThrowIfTaskFailed(task);
        return task;
    }

    private async Task<(PiApiTaskResult Create, PiApiTaskResult Result)> CreateAndWaitForMediaTaskAsync(
        string model,
        string defaultTaskType,
        Dictionary<string, object?> input,
        Dictionary<string, JsonElement>? providerOptions,
        CancellationToken cancellationToken)
    {
        var create = await CreateMediaTaskAsync(
            model,
            defaultTaskType,
            input,
            providerOptions,
            cancellationToken);

        if (IsCompletedTask(create.Status))
            return (create, create);

        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException("PiAPI task creation response did not contain data.task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetMediaTaskAsync(create.TaskId!, ct),
            isTerminal: result => IsTerminalTask(result.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        ThrowIfTaskFailed(completed);
        if (!IsCompletedTask(completed.Status))
            throw new InvalidOperationException($"PiAPI task ended with unsupported terminal status '{completed.Status}'.");

        return (create, completed);
    }

    private async Task<PiApiTaskResult> GetMediaTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PiApiTaskEndpoint}/{Uri.EscapeDataString(taskId)}");
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PiAPI task polling failed ({(int)response.StatusCode}): {raw}");

        return ParseTaskResult(raw);
    }

    private static PiApiTaskResult ParseTaskResult(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var data = GetData(root);
        var taskId = GetString(data, "task_id") ?? GetString(data, "id");
        var status = GetString(data, "status") ?? GetString(root, "status");

        return new PiApiTaskResult(taskId, status, root);
    }

    private static bool IsCompletedTask(string? status)
        => status is not null && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase));

    private static bool IsTerminalTask(string? status)
        => IsCompletedTask(status) || (status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase)));

    private static void ThrowIfTaskFailed(PiApiTaskResult task)
    {
        if (task.Status is null || !task.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            && !task.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            && !task.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            && !task.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            && !task.Status.Equals("expired", StringComparison.OrdinalIgnoreCase))
            return;

        var data = GetData(task.Root);
        var error = data.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null
            ? errorElement.GetRawText()
            : task.Root.GetRawText();
        throw new InvalidOperationException($"PiAPI task failed with status '{task.Status}': {error}");
    }

    private static JsonElement GetData(JsonElement root)
        => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, JsonElement>? GetPiApiOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue(nameof(PiAPI).ToLowerInvariant(), out var options)
            || options.ValueKind != JsonValueKind.Object)
            return null;

        return options.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static string? GetStringOption(Dictionary<string, JsonElement>? options, string name)
        => options is not null && options.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?>? GetObjectOption(Dictionary<string, JsonElement>? options, string name)
    {
        if (options is null || !options.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        return value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    private static void MergeJsonObject(Dictionary<string, object?> target, Dictionary<string, object?>? source)
    {
        if (source is null)
            return;

        foreach (var (key, value) in source)
            target[key] = value;
    }

    private string ToPiApiModelId(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private Dictionary<string, JsonElement> CreateMediaProviderMetadata(PiApiTaskResult create, PiApiTaskResult result)
        => GetIdentifier().CreatePrimitiveProviderMetadata(new Dictionary<string, JsonElement>
        {
            ["create"] = create.Root,
            ["result"] = result.Root
        });

    private static IEnumerable<string> GetOutputValues(JsonElement root, params string[] names)
    {
        var data = GetData(root);
        var output = data.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.Object
            ? outputElement
            : data.TryGetProperty("task_result", out var resultElement)
                && resultElement.TryGetProperty("task_output", out var taskOutput)
                && taskOutput.ValueKind == JsonValueKind.Object
                    ? taskOutput
                    : default;

        if (output.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var name in names)
        {
            if (!output.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                yield return value.GetString()!;
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        yield return item.GetString()!;
            }
        }
    }

    private async Task<(string Base64, string MimeType)> DownloadMediaAsync(
        string value,
        string fallbackMimeType,
        CancellationToken cancellationToken)
    {
        if (TryParseDataUrl(value, out var dataUrlMimeType, out var dataUrlBase64))
            return (dataUrlBase64, dataUrlMimeType ?? fallbackMimeType);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (value, fallbackMimeType);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = null;
        using var response = await _client.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"PiAPI media download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMediaType(uri.AbsolutePath, fallbackMimeType);
        return (Convert.ToBase64String(bytes), mimeType);
    }

    private static bool TryParseDataUrl(string value, out string? mimeType, out string base64)
    {
        mimeType = null;
        base64 = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 5)
            return false;

        mimeType = value[5..marker];
        base64 = value[(marker + ";base64,".Length)..];
        return true;
    }

    private static string GuessMediaType(string value, string fallback)
        => value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
            : value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
            : value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
            : value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4"
            : value.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm"
            : value.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime"
            : value.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav"
            : value.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg"
            : fallback;

    private static string ToDataUrl(string base64, string mimeType)
        => $"data:{mimeType};base64,{base64}";
}
