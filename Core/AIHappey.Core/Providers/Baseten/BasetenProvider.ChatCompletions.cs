using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using OAIC = OpenAI.Chat;

namespace AIHappey.Core.Providers.Baseten;

public sealed partial class BasetenProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

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
}

