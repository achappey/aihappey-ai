using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private async Task<JsonElement> SendNativeAsync(object payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cortecs error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private async IAsyncEnumerable<JsonElement> SendNativeStreamingAsync(
        object payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Cortecs stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

         string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line is null) yield break;
            if (line.Length == 0) continue;
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data is "[DONE]" or "[done]") yield break;

            JsonElement chunk;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("data", out var inner)
                    && inner.ValueKind == JsonValueKind.Object)
                {
                    chunk = inner.Clone();
                }
                else
                {
                    chunk = root.Clone();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            yield return chunk;
        }
    }

    private static void AddIfPresent(JsonElement root, IDictionary<string, object?> payload, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return;

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        payload[propertyName] = JsonSerializer.Deserialize<object>(prop.GetRawText(), JsonSerializerOptions.Web);
    }
}

