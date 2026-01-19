using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AIHappey.Core.AI;

public static class RealtimeExtensions
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<T> GetRealtimeResponse<T>(
        this HttpClient client,
        JsonElement payload,
        string relativeUrl = "v1/realtime/client_secrets",
        CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var contents = await resp.Content.ReadAsStringAsync(ct);

            throw new Exception(contents);
        }

        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct) 
            ?? throw new Exception("No content");
    }

}



