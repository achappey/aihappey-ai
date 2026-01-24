using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.CloudRift;

public sealed partial class CloudRiftProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions chatRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // Keep behavior consistent with other OpenAI-compatible providers.
        chatRequest.ParallelToolCalls ??= true;

        var json = JsonSerializer.Serialize(chatRequest, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json);

        using var resp = await _client.PostAsync("chat/completions", content, cancellationToken);
        var result = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(result);

        var respJson = JsonSerializer.Deserialize<ChatCompletion>(result, JsonOpts);
        return respJson ?? throw new Exception("Something went wrong");
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        options.Stream = true;
        options.ParallelToolCalls ??= true;

        var json = JsonSerializer.Serialize(options, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception(err);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data: ".Length..].Trim();
            if (data is "[DONE]" or "[done]")
                yield break;

            var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, JsonOpts);
            if (update is not null)
                yield return update;
        }
    }
}
