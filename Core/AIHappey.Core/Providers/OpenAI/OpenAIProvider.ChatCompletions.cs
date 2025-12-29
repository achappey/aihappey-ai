using OpenAI;
using OAIC = OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = new OpenAIClient(GetKey()).GetChatClient(options.Model);

        // OpenAI .NET SDK should offer streaming. If it yields tokens or parts, yield those.
        IEnumerable<OAIC.ChatMessage> oaiMessages = JsonSerializer.Deserialize<IEnumerable<OAIC.ChatMessage>>(
                     JsonSerializer.Serialize(options.Messages, JsonSerializerOptions.Web))!;

        await foreach (var chunk in client.CompleteChatStreamingAsync(oaiMessages, new OAIC.ChatCompletionOptions()
        {
            AllowParallelToolCalls = options.ParallelToolCalls,
            Temperature = options.Temperature
        },
            cancellationToken: cancellationToken))
        {
            yield return chunk;
        }
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest, CancellationToken cancellationToken = default)
    {
        if (!_client.DefaultRequestHeaders.Contains("Authorization"))
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetKey());

        chatRequest.ParallelToolCalls ??= true;

        var json = JsonSerializer.Serialize(chatRequest, JsonSerializerOptions.Web);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // -----------------------------
        // Send raw HTTP POST
        // -----------------------------
        using var resp = await _client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            content,
            cancellationToken);

        var result = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(result);

        var respJson = JsonSerializer.Deserialize<ChatCompletion>(result);
        return respJson ?? throw new Exception("Something went wrong");
    }
}
