using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // Build inputs from your UI message model (text + image_url only)
        var inputs = chatRequest.Messages
            // .Where(m => m.Role != Role.system)
            .Select(m => new
            {
                type = "message.input",
                role = m.Role, // your API expects "user"/"assistant" — your model already uses those literals
                content = m.Content.Select(a => a is TextContentBlock tcb
                    ? (object)new { type = "text", text = tcb.Text }
                    : a is ImageContentBlock icb
                        ? (object)new { type = "image_url", image_url = icb.ToDataUrl() }
                        : (object?)null).Where(a => a != null)
            });

        var tools = new List<object>();

        var codeInterpreter = chatRequest.Metadata.ToCodeInterpreter();
        if (codeInterpreter != null)
        {
            tools.Add(codeInterpreter);
        }

        var imageGeneration = chatRequest.Metadata.ToImageGeneration();
        if (imageGeneration != null)
        {
            tools.Add(imageGeneration);
        }
        var webSearchPremium = chatRequest.Metadata.ToWebSearchPremiumTool();
        if (webSearchPremium != null)
        {
            tools.Add(webSearchPremium);
        }
        else
        {
            var webSearch = chatRequest.Metadata.ToWebSearchTool();
            if (webSearch != null)
            {
                tools.Add(webSearch);
            }
        }

        var payload = new JsonObject
        {
            ["model"] = chatRequest.GetModel(),
            ["stream"] = false,
            ["store"] = false,
            ["instructions"] = chatRequest.SystemPrompt,
            ["inputs"] = JsonSerializer.SerializeToNode(inputs.ToArray()),
            ["completion_args"] = new JsonObject
            {
                ["temperature"] = chatRequest.Temperature,
                ["max_tokens"] = chatRequest.MaxTokens
            }
        };

        if (tools.Count > 0)
        {
            payload["tools"] = JsonSerializer.SerializeToNode(tools);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            // Return a clean error result (consistent with your style)
            return new CreateMessageResult
            {
                Role = Role.Assistant,
                Model = chatRequest.GetModel() ?? "mistral",
                StopReason = "error",
                Content = [$"Mistral conversations error: {(string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body)}"
                    .ToTextContentBlock()]
            };
        }

        var json = JsonNode.Parse(body);
        var conversationId = json?["conversation_id"]?.GetValue<string>();
        var usage = json?["usage"];
        var outputs = json?["outputs"]?.AsArray();

        // Defaults
        string resolvedModel = chatRequest.GetModel() ?? "mistral";
        string accumulatedText = string.Empty;
        List<ContentBlock> contentBlocks = [];

        if (outputs is not null)
        {
            // Prefer the first assistant message.output entry
            var firstOut = outputs
                .FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message.output");

            if (firstOut is not null)
            {
                resolvedModel = firstOut?["model"]?.GetValue<string>() ?? resolvedModel;

                var contentNode = firstOut?["content"];
                if (contentNode is null)
                {
                    accumulatedText = string.Empty;
                }
                else if (contentNode.GetValueKind() == JsonValueKind.String)
                {
                    // Simple string content
                    accumulatedText = contentNode!.GetValue<string>() ?? string.Empty;
                }
                else if (contentNode.GetValueKind() == JsonValueKind.Array)
                {
                    // Iterate chunks: text + possible tool_file
                    foreach (var chunk in contentNode!.AsArray())
                    {
                        var ctype = chunk?["type"]?.GetValue<string>();

                        // Text chunks (Mistral may use "output_text" or "text")
                        if (ctype == "output_text" || ctype == "text")
                        {
                            var txt = chunk?["text"]?.GetValue<string>() ?? chunk?["content"]?.GetValue<string>() ?? string.Empty;
                            if (!string.IsNullOrEmpty(txt))
                                contentBlocks.Add(txt.ToTextContentBlock());
                        }

                        // Tool file chunks: download and convert to EmbeddedResourceBlock
                        if (ctype == "tool_file")
                        {
                            var fileId = chunk?["file_id"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(fileId))
                            {
                                // GET /v1/files/{file_id}
                                using var metaReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}");
                                using var metaResp = await _client.SendAsync(metaReq, cancellationToken);
                                if (!metaResp.IsSuccessStatusCode)
                                {
                                    // If metadata fails, continue with text result only
                                    continue;
                                }
                                var metaStr = await metaResp.Content.ReadAsStringAsync(cancellationToken);
                                var metaJson = JsonNode.Parse(metaStr);
                                var deleted = metaJson?["deleted"]?.GetValue<bool?>() ?? false;
                                if (deleted) continue;

                                var mime = metaJson?["mimetype"]?.GetValue<string?>()
                                           ?? chunk?["file_type"]?.GetValue<string?>()
                                           ?? MediaTypeNames.Application.Octet;

                                // GET /v1/files/{file_id}/content
                                using var fileReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}/content");
                                using var fileResp = await _client.SendAsync(fileReq, cancellationToken);
                                if (!fileResp.IsSuccessStatusCode) continue;

                                var bytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
                                var base64 = Convert.ToBase64String(bytes);

                                contentBlocks.Add(new EmbeddedResourceBlock
                                {
                                    Resource = new BlobResourceContents
                                    {
                                        Uri = $"https://api.mistral.ai/v1/files/{fileId}",
                                        MimeType = mime,
                                        Blob = base64
                                    }
                                });
                                // If we got a file, prefer returning it (you can still keep text in meta if you want)
                            }
                        }

                        // Optional: surface tool/document references in accumulatedText (skip for now)
                        // if (ctype == "tool_reference" || ctype == "document_url") { ... }
                    }
                }
            }
        }

        // Usage → meta
        var promptTokens = usage?["prompt_tokens"]?.GetValue<int?>() ?? 0;
        var completionTokens = usage?["completion_tokens"]?.GetValue<int?>() ?? 0;
        var totalTokens = usage?["total_tokens"]?.GetValue<int?>() ?? (promptTokens + completionTokens);

        var meta = new JsonObject
        {
            ["inputTokens"] = promptTokens,
            ["totalTokens"] = totalTokens
        };

        if (!string.IsNullOrWhiteSpace(conversationId))
            meta["conversationId"] = conversationId;

        ContentBlock resultBlock = contentBlocks.OfType<EmbeddedResourceBlock>().Any() ?
            contentBlocks.OfType<EmbeddedResourceBlock>().First()
            : string.Join(Environment.NewLine, contentBlocks.OfType<TextContentBlock>().Select(a => a.Text)).ToTextContentBlock()
            ?? throw new Exception("No content");

        return new()
        {
            Role = Role.Assistant,
            Model = resolvedModel,
            StopReason = "stop",
            Content = [resultBlock],
            Meta = meta
        };
    }
}