using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider
{
    private HttpRequestMessage CreateMessage(HttpMethod method, string path, object? payload = null,
        bool streaming = false, bool bufferedSpeech = false)
    {
        var message = new HttpRequestMessage(method, path);
        var key = _keys.Resolve(ProviderId);
        if (!string.IsNullOrWhiteSpace(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Accept.Add(new(streaming ? "text/event-stream" : "application/json"));
        if (bufferedSpeech)
            message.Headers.Add("buffered-speech", "1");
        if (payload is not null)
            message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json");
        return message;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var retry = response.Headers.RetryAfter?.ToString();
        throw new HttpRequestException(
            $"EdgeSpeak request failed ({(int)response.StatusCode}): {body}" +
            (retry is null ? "" : $" Retry-After: {retry}"),
            null, response.StatusCode);
    }

    private async Task<JsonElement> GetJson(string path, CancellationToken ct)
    {
        using var request = CreateMessage(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private sealed record NativeEvent(string Type, JsonElement Data, IDictionary<string, string>? Headers = null);

    private static async IAsyncEnumerable<NativeEvent> ReadEvents(
        HttpResponseMessage response, string terminal, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EdgeSpeak returned a non-SSE streaming response.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var name = "";
        var data = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    using var document = JsonDocument.Parse(string.Join("\n", data));
                    var root = document.RootElement.Clone();
                    var payloadType = String(root, "type");
                    var type = name.Length > 0 && name != "message" ? name : payloadType ?? "";
                    if (type == "error" || payloadType == "error" ||
                        root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                        throw new InvalidOperationException($"EdgeSpeak stream failed: {root.GetRawText()}");
                    if (name.Length > 0 && name != "message" && payloadType is not null && payloadType != name)
                        throw new InvalidOperationException($"EdgeSpeak returned conflicting SSE event types: {name} and {payloadType}.");
                    yield return new(type, root);
                    ct.ThrowIfCancellationRequested();
                    if (type == terminal)
                        yield break;
                }
                name = "";
                data.Clear();
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            if (field == "event")
                name = value;
            else if (field == "data")
                data.Add(value);
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException($"EdgeSpeak stream disconnected before '{terminal}'.");
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
            field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string NativeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var native = model.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(ProviderId.Length + 1)..] : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(native);
        return native;
    }

    private static Dictionary<string, JsonElement> NativeOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue(ProviderId, out var native) != true)
            return new(StringComparer.Ordinal);
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.edgespeak must be an object.");
        return native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement> ExtensionOptions(Dictionary<string, JsonElement>? additional)
    {
        var result = new Dictionary<string, JsonElement>(additional ?? [], StringComparer.Ordinal);
        if (result.Remove("providerOptions", out var wrapper))
        {
            if (wrapper.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions must be an object.");
            if (wrapper.TryGetProperty(ProviderId, out var options))
                foreach (var entry in NativeOptions(new() { [ProviderId] = options }))
                    result[entry.Key] = entry.Value;
        }
        if (result.Remove(ProviderId, out var scoped))
            foreach (var entry in NativeOptions(new() { [ProviderId] = scoped }))
                result[entry.Key] = entry.Value;
        return result;
    }

    private static Dictionary<string, JsonElement> Metadata(JsonElement value)
        => new() { [ProviderId] = value.Clone() };
}
