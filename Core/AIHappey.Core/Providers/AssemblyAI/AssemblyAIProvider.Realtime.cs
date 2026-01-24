using AIHappey.Common.Model;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace AIHappey.Core.Providers.AssemblyAI;
public partial class AssemblyAIProvider 
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://streaming.assemblyai.com/v3/token?expires_in_seconds=600");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var contents = await resp.Content.ReadAsStringAsync(cancellationToken);

            throw new Exception(contents);
        }

        var token = await resp.Content.ReadFromJsonAsync<AssemblyAITokenResponse>(cancellationToken)
            ?? throw new Exception();

        return new RealtimeResponse()
        {
            Value = token.Token,
            ExpiresAt = DateTimeOffset.UtcNow
                .AddMinutes(token.ExpiresInSeconds)
                .ToUnixTimeSeconds(),
        };
    }
}

public class AssemblyAITokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = null!;

    [JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; set; }

}
