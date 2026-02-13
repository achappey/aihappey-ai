using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text;
using System.Dynamic;
using System.Net.Mime;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        
        var tools = chatRequest.GetTools();

        dynamic payload = new ExpandoObject();
        payload.model = chatRequest.GetModel();
        payload.stream = false;
        payload.temperature = chatRequest.Temperature;
        payload.reasoning = chatRequest.Metadata.ToReasoning();
        payload.store = false;
        payload.input = chatRequest.Messages.BuildSamplingInput();
        payload.tools = tools;

        if (tools.Count > 0)
            payload.tool_choice = "auto";

        // Serialize payload
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // -------- Extract text content --------
        var root = doc.RootElement;
        var sb = new StringBuilder();

        if (root.TryGetProperty("output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in outputArray.EnumerateArray())
            {
                if (message.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in contentArray.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var textProp))
                        {
                            sb.AppendLine(textProp.GetString());
                        }
                    }
                }
            }
        }

        // -------- Extract usage --------
        int? inputTokens = null, totalTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var promptTokens))
                inputTokens = promptTokens.GetInt32();

            if (usage.TryGetProperty("total_tokens", out var completion))
                totalTokens = completion.GetInt32();
        }

        // -------- Return result --------
        return new()
        {
            Model = payload.model,
            Role = Role.Assistant,
            Content = [sb.ToString().Trim().ToTextContentBlock()],
            Meta = new System.Text.Json.Nodes.JsonObject
            {
                ["inputTokens"] = inputTokens,
                ["totalTokens"] = totalTokens
            }
        };
    }

}