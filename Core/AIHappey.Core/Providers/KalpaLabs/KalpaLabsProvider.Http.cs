using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private HttpRequestMessage CreateMessage(HttpMethod method, string path, object? payload = null, string accept = "application/json")
    {
        var key = _keyResolver.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        var message = new HttpRequestMessage(method, path);
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            message.Headers.Accept.ParseAdd(accept);
            if (payload is not null)
                message.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var id = response.Headers.TryGetValues("X-Request-ID", out var ids) ? string.Join(", ", ids) : null;
        throw new HttpRequestException(
            $"{ProviderName} failed ({(int)response.StatusCode}, request_id={id}, Retry-After={response.Headers.RetryAfter}): {body}",
            null, response.StatusCode);
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var message = CreateMessage(method, path, payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return document.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Metadata(JsonElement value) => new() { [ProviderId] = value.Clone() };
}
