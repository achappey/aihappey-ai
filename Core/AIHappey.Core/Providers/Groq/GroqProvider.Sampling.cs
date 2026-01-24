using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // Prepare tools (function defs) from your metadata / helpers
        var tools = chatRequest.GetTools();

        // Build Groq Responses payload (non-streaming)
        // Uses your existing helpers for model, input, temperature, reasoning, etc.
        var payload = new
        {
            model = chatRequest.GetModel(),                         // e.g., "openai/gpt-oss-120b"
            stream = false,                                         // non-streaming
            store = false,                                          // per Groq docs (only false/null currently supported)
            temperature = chatRequest.Temperature,                  // nullable okay
            truncation = "auto",                                                     // max_output_tokens = chatRequest.MaxTokens,              // nullable okay
            instructions = chatRequest.SystemPrompt,                // optional system/dev message
            reasoning = chatRequest.Metadata.ToReasoning(),         // your helper → object or null
            input = chatRequest.Messages.BuildSamplingInput(),      // your helper builds a text/array input
            tools = tools.Count == 0 ? null : tools,                                         // tools = tools.Count == 0 ? null : tools,                // omit when empty
            tool_choice = tools.Count == 0 ? null : "auto",                                                    //  tool_choice = tools.Count == 0 ? null : "auto",         // allow model to call tools if present
        };

        // Serialize
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "openai/v1/responses")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            // Return a clean, readable error result (consistent with your other providers)
            return new CreateMessageResult
            {
                Model = payload.model ?? GroqExtensions.Identifier(),
                Role = Role.Assistant,
                StopReason = "error",
                Content = [$"Groq responses error: {(string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body)}"
                    .ToTextContentBlock()]
            };
        }

        // Parse JSON
        var root = JsonNode.Parse(body);

        // -------- Extract assistant text --------
        List<ContentBlock> contentBlocks = [];

        var outputArray = root?["output"]?.AsArray();

        if (outputArray is not null)
        {
            foreach (var message in outputArray)
            {
                var content = message?["content"]?.AsArray();
                if (content is null) continue;

                foreach (var part in content)
                {
                    var type = part?["type"]?.GetValue<string>();
                    if (type == "output_text")
                    {
                        var txt = part?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(txt))
                            contentBlocks.Add(txt.ToTextContentBlock());
                    }
                    // If you later want to support tool call returns, inspect other part types here.
                }
            }
        }

        // -------- Extract usage --------
        int? inputTokens = null, totalTokens = null;
        var usage = root?["usage"];
        if (usage is not null)
        {
            // Groq Responses example fields:
            // "input_tokens", "output_tokens", "total_tokens"
            inputTokens = usage?["input_tokens"]?.GetValue<int?>();
            totalTokens = usage?["total_tokens"]?.GetValue<int?>();
        }

        // Model echo (Groq returns "model" at top level)
        var resolvedModel = root?["model"]?.GetValue<string>() ?? payload.model ?? GroqExtensions.Identifier();

        return new CreateMessageResult
        {
            Role = Role.Assistant,
            Model = resolvedModel,
            StopReason = "stop",
            Content = contentBlocks,
            Meta = new JsonObject
            {
                ["inputTokens"] = inputTokens,
                ["totalTokens"] = totalTokens
            }
        };
    }
}