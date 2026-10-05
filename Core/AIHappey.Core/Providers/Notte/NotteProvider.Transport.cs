using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Notte;

public partial class NotteProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Reply(JsonElement Raw, Dictionary<string, string[]> Headers);

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    private HttpRequestMessage Message(HttpMethod method, string path, AIRequest request, object? body = null)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Notte API key.");
        var message = new HttpRequestMessage(method, new Uri(new Uri("https://api.notte.cc/"), path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var name in new[] { "x-notte-request-origin", "x-notte-sdk-version" })
            if (request.Headers?.TryGetValue(name, out var value) == true)
            {
                if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Invalid Notte header.");
                message.Headers.TryAddWithoutValidation(name, value);
            }
        if (body is not null) message.Content = JsonContent.Create(body, options: Json);
        return message;
    }

    private async Task<Reply> Send(AIRequest request, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = Message(method, path, request, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var bytes = await ReadBounded(response.Content, 16 * 1024 * 1024, timeout.Token);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (!response.IsSuccessStatusCode)
        {
            var error = new HttpRequestException($"Notte {path} failed ({(int)response.StatusCode}): {text}", null, response.StatusCode);
            error.Data["notte.headers"] = headers;
            throw error;
        }
        using var document = JsonDocument.Parse(text);
        return new(document.RootElement.Clone(), headers);
    }

    private static async Task<byte[]> ReadBounded(HttpContent content, long limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidOperationException("Notte response exceeds gateway size limits.");
        await using var source = await content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (target.Length + count > limit) throw new InvalidOperationException("Notte response exceeds gateway size limits.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return target.ToArray();
    }

    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("notte", out var direct) == true) return Object(direct);
        foreach (var key in new[] { "chatcompletions.request.metadata", "chatcompletions.request.provider_metadata", "messages.request.metadata", "messages.request.provider_metadata" })
            if (request.Metadata?.TryGetValue(key, out var value) == true && value is not null)
            {
                var raw = JsonSerializer.SerializeToElement(value, Json);
                if (Property(raw, "notte") is { } scoped) return Object(scoped);
            }
        return new();
    }

    private static JsonObject Object(object? value)
        => value is null ? new() : JsonNode.Parse(JsonSerializer.Serialize(value, Json)) as JsonObject
            ?? throw new ArgumentException("Notte provider options must be an object.");
    private static JsonElement? Property(JsonElement raw, string name)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value) ? value : null;
    private static string? String(JsonElement raw, string name)
        => Property(raw, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static AIInputItem? LatestUser(AIRequest request)
        => request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
    private static string Text(AIRequest request)
    {
        var text = string.Join("\n", LatestUser(request)?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        if (string.IsNullOrWhiteSpace(text)) text = request.Input?.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Notte requires non-empty latest user text.");
        return text;
    }
    private static double Control(JsonObject controls, string name, double fallback, double min, double max)
    {
        var value = controls[name]?.GetValue<double>() ?? fallback;
        if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException($"Notte gateway.{name} must be between {min} and {max}.");
        return value;
    }
}
