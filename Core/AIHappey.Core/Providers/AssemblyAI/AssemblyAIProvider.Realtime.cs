using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Net.Http.Json;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider : IModelProvider
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
