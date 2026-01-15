using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Nodes;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider : IModelProvider
{
    public Task<Common.Model.ImageResponse> ImageRequest(Common.Model.ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var payload = new
        {
            model = chatRequest.GetModel(),
            stream = false,
        };

        // Serialize
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
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
                Model = payload.model ?? "jina",
                Role = Role.Assistant,
                StopReason = "error",
                Content = [$"Jina responses error: {(string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body)}"
                    .ToTextContentBlock()]
            };
        }

        // Parse JSON
        var root = JsonNode.Parse(body);

        // -------- Extract assistant text --------
        var sb = new StringBuilder();
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
                            sb.AppendLine(txt);
                    }
                    // If you later want to support tool call returns, inspect other part types here.
                }
            }
        }

        var textOut = sb.ToString().Trim();
        if (string.IsNullOrEmpty(textOut))
            textOut = string.Empty;

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
        var resolvedModel = root?["model"]?.GetValue<string>() ?? payload.model ?? "jina";

        return new CreateMessageResult
        {
            Role = Role.Assistant,
            Model = resolvedModel,
            StopReason = "stop",
            Content = [textOut.ToTextContentBlock()],
            Meta = new JsonObject
            {
                ["inputTokens"] = inputTokens,
                ["totalTokens"] = totalTokens
            }
        };
    }

    public Task<Common.Model.SpeechResponse> SpeechRequest(Common.Model.SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.TranscriptionResponse> TranscriptionRequest(Common.Model.TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}