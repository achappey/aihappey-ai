using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    private static readonly JsonSerializerOptions TemboJson = JsonSerializerOptions.Web;
    private string ResolveKey() => _keyResolver.Resolve(GetIdentifier()) is { Length: > 0 } key
        && !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidOperationException("No Tembo API key.");

    private async Task<JsonElement> SendJsonAsync(string key, HttpMethod method, string path,
        JsonElement? payload, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload.HasValue)
                request.Content = new StringContent(payload.Value.GetRawText(), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (method == HttpMethod.Get && attempt < 2
                && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250 * (attempt + 1));
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero
                    : delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Tembo {method} {path.Split('?')[0]} failed with HTTP {(int)response.StatusCode}.",
                    null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Tembo {path.Split('?')[0]} returned a non-object JSON response.");
            return document.RootElement.Clone();
        }
    }

    private async Task<List<JsonElement>> ListItemsAsync(string key, string path, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var url = path + (path.Contains('?') ? "&" : "?") + "limit=50"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await SendJsonAsync(key, HttpMethod.Get, url, null, cancellationToken);
            if (Property(page, "items") is not { ValueKind: JsonValueKind.Array } items)
                throw new InvalidOperationException($"Tembo {path.Split('?')[0]} response is missing items.");
            result.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            if (!page.TryGetProperty("nextCursor", out var next) || next.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidOperationException($"Tembo {path.Split('?')[0]} response has an invalid nextCursor.");
            cursor = next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidOperationException($"Tembo {path.Split('?')[0]} pagination repeated a cursor.");
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }

    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;
    private static string RequiredString(JsonElement value, string name)
        => String(value, name) is { Length: > 0 } result ? result
            : throw new InvalidOperationException($"Tembo response is missing '{name}'.");
    private static bool IsTrue(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.True };
}
