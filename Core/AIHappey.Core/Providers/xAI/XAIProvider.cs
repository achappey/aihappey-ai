using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using OAIC = OpenAI.Chat;
using Tool = AIHappey.Common.Model.Tool;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    public XAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;

        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.x.ai/");

    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(xAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    

    // Helper: extract delta text from a variety of Responses event payload shapes
    private static string? TryGetDeltaText(JsonElement root)
    {
        // Shape A: { "delta": "..." }
        if (root.TryGetProperty("delta", out var d1) && d1.ValueKind == JsonValueKind.String)
            return d1.GetString();

        // Shape B: { "output_text": { "delta": "..." } }
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.Object)
            if (ot.TryGetProperty("delta", out var d2) && d2.ValueKind == JsonValueKind.String)
                return d2.GetString();

        // Shape C (rare): { "content": [{ "type":"output_text","text":"..." }]} — fallback to full text if no true delta
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in content.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() is string ty &&
                    (ty.Equals("output_text", StringComparison.OrdinalIgnoreCase) ||
                     ty.Equals("text", StringComparison.OrdinalIgnoreCase)))
                {
                    if (c.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        return txt.GetString();
                }
            }
        }

        return null;
    }
    private readonly HashSet<string> _startedIds = new(StringComparer.Ordinal);

    private static IEnumerable<UIMessagePart> ReadToolCallPart(JsonElement el, bool providerExecuted, IEnumerable<Tool> tools)
    {
        if (el.ValueKind != JsonValueKind.Object)
            yield break;

        if (!el.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "response.output_item.done")
            yield break;

        if (!el.TryGetProperty("item", out var itemEl) || itemEl.ValueKind != JsonValueKind.Object)
            yield break;

        string? toolCallId = null;
        string? toolName = null;
        string? input = null;

        if (itemEl.TryGetProperty("id", out var idEl))
            toolCallId = idEl.GetString();

        if (itemEl.TryGetProperty("name", out var nameEl))
            toolName = nameEl.GetString();

        if (itemEl.TryGetProperty("arguments", out var argsEl))
            input = argsEl.GetString();

        if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(toolName))
            yield break;

        // 1️⃣ Tool call (execution started)
        yield return new ToolCallPart
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            Title = tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
            ProviderExecuted = providerExecuted,
            Input = !string.IsNullOrEmpty(input)
                ? JsonSerializer.Deserialize<object>(input!)!
                : new object()
        };

        if (providerExecuted)
        {
            // 2️⃣ Tool output (execution completed)
            yield return new ToolOutputAvailablePart
            {
                ToolCallId = toolCallId,
                ProviderExecuted = providerExecuted,
                Output = new CallToolResult()
                {
                    IsError = false,
                    Content = ["Server-side tool call outputs are not returned in the API response. The agent uses these outputs internally to formulate its final response, but they are not exposed here.".ToTextContentBlock()]
                }
            };
        }
        else
        {
            yield return new ToolApprovalRequestPart
            {
                ToolCallId = toolCallId,
                ApprovalId = Guid.NewGuid().ToString(),
            };
        }
    }

    private static void ReadUsage(JsonElement el,
        ref int inputTokens, ref int outputTokens, ref int totalTokens, ref int? reasoningTokens)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        if (el.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptVal)) inputTokens = ptVal;
            if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctVal)) outputTokens = ctVal;
            if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.TryGetInt32(out var ttVal)) totalTokens = ttVal;

            // xAI sometimes nests reasoning here:
            if (usageEl.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
                if (ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt32(out var rtVal))
                    reasoningTokens = rtVal;

            // Some variants place it at top-level "reasoning_tokens"
            if (usageEl.TryGetProperty("reasoning_tokens", out var rt2) && rt2.TryGetInt32(out var rt2Val))
                reasoningTokens = rt2Val;
        }
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}