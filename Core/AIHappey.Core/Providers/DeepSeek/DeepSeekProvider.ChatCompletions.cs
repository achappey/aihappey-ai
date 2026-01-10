using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using OAIC = OpenAI.Chat;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
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

        var json = JsonSerializer.Serialize(chatRequest, JsonSerializerOptions.Web);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _client.PostAsync("chat/completions", content, cancellationToken);
        var result = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(result);

        var respJson = JsonSerializer.Deserialize<ChatCompletion>(result, JsonSerializerOptions.Web);
        return respJson ?? throw new Exception("Something went wrong");
    }
}

