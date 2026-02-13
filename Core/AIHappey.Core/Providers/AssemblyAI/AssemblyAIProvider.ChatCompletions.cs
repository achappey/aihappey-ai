using AIHappey.Core.AI;
using AIHappey.Common.Model.ChatCompletions;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider 
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader(_llmGatewayClient);

        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var mappedMessages = options.Messages.ToAssemblyAIMessages()
                  .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["parallel_tool_calls"] = options.ParallelToolCalls ?? true,
            ["messages"] = mappedMessages,
            ["tools"] = options.Tools,
            ["temperature"] = options.Temperature,
        };

        if (options.Tools.Count() > 0)
            payload["tool_choice"] = options.ToolChoice ?? "auto";

        if (options.ResponseFormat is not null)
            payload["response_format"] = options.ResponseFormat;

        return await _llmGatewayClient.GetChatCompletion(
            JsonSerializer.SerializeToElement(payload),
            ct: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
         ChatCompletionOptions options,
         [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // AssemblyAI LLM Gateway does NOT support server-side streaming.
        // We simulate streaming by performing a non-stream request and converting the single
        // response into a small sequence of OpenAI-style chunks.
        var result = await CompleteChatAsync(options, cancellationToken).ConfigureAwait(false);

        var id = string.IsNullOrWhiteSpace(result.Id) ? Guid.NewGuid().ToString("n") : result.Id;
        var created = result.Created != 0 ? result.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = string.IsNullOrWhiteSpace(result.Model) ? options.Model : result.Model;

        // Extract first choice content/tool_calls/finish_reason from the opaque Choices payload.
        string? content = null;
        object? toolCalls = null;
        string? finishReason = "stop";

        var firstChoice = result.Choices?.FirstOrDefault();
        if (firstChoice is JsonElement chEl && chEl.ValueKind == JsonValueKind.Object)
        {
            if (chEl.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                finishReason = frEl.GetString();

            // tool_calls can be at choice level or within message
            if (chEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                toolCalls = JsonSerializer.Deserialize<object>(tcEl.GetRawText(), JsonSerializerOptions.Web);

            if (chEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                if (msgEl.TryGetProperty("content", out var cEl))
                    content = cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : cEl.GetRawText();

                if (toolCalls is null && msgEl.TryGetProperty("tool_calls", out var mtcEl)
                    && mtcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    toolCalls = JsonSerializer.Deserialize<object>(mtcEl.GetRawText(), JsonSerializerOptions.Web);
                }
            }
        }

        // Chunk 1: role
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ],
            Usage = null
        };

        // Chunk 2: full content (single delta) + optional tool_calls
        var deltaObj = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(content))
            deltaObj["content"] = content;
        if (toolCalls is not null)
            deltaObj["tool_calls"] = toolCalls;

        if (deltaObj.Count > 0)
        {
            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new { index = 0, delta = deltaObj, finish_reason = (string?)null }
                ],
                Usage = null
            };
        }

        // Chunk 3: final
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = finishReason ?? "stop" }
            ],
            Usage = result.Usage
        };
    }
}


