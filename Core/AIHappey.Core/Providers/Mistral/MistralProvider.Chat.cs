using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("voxtral"))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        var url = "/v1/conversations";

        ApplyAuthHeader();

        var inputs = chatRequest.Messages
            .Where(a => a.Role != Role.system)
            .SelectMany(m => m.ToMistralMessages()) // preserves natural chronological order
            .ToList();

        var system = string.Join("\n\n", chatRequest.Messages
            .Where(a => a.Role == Role.system)
            .SelectMany(a => a.Parts.OfType<TextUIPart>().Select(a => a.Text)));

        List<dynamic>? tools = chatRequest.Tools?
            .Select(a => new
            {
                type = "function",
                function = new
                {
                    name = a.Name,
                    description = a.Description,
                    parameters = a.InputSchema
                }
            })
            .Cast<dynamic>()
            .ToList() ?? [];


        var metadata = chatRequest.GetProviderMetadata<MistralProviderMetadata>(GetIdentifier());

        if (metadata?.CodeInterpreter != null)
        {
            tools.Add(metadata.CodeInterpreter);
        }

        if (metadata?.ImageGeneration != null)
        {
            tools.Add(metadata.ImageGeneration);
        }

        if (metadata?.DocumentLibrary != null)
        {
            tools.Add(metadata.DocumentLibrary);
        }

        if (metadata?.WebSearchPremium != null)
        {
            tools.Add(metadata.WebSearchPremium);
        }
        else if (metadata?.WebSearch != null)
        {
            tools.Add(metadata.WebSearch);
        }

        var payload = new JsonObject
        {
            ["model"] = chatRequest.Model,
            ["stream"] = true,
            ["store"] = false,
            ["instructions"] = system,
            ["inputs"] = JsonSerializer.SerializeToNode(inputs.ToArray()),
            ["completion_args"] = new JsonObject
            {
                ["temperature"] = chatRequest.Temperature,
                ["max_tokens"] = chatRequest.MaxOutputTokens
            }
        };

        if (tools.Count > 0)
        {
            payload["tools"] = JsonSerializer.SerializeToNode(tools);
        }



        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json),
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Mistral stream error: {(string.IsNullOrWhiteSpace(errBody) ? resp.ReasonPhrase : errBody)}"
                .ToErrorUIPart();

            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? sseEvent = null;
        string? streamId = null;
        string? toolStreamId = null;
        string toolInput = string.Empty;
        string streamingToolName = string.Empty;
        bool textStarted = false;
        bool sawDone = false;

        string modelId = chatRequest.Model;
        int inputTokens = 0, outputTokens = 0, totalTokens = 0;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) { sseEvent = null; continue; }     // event separator
            if (line.StartsWith(':')) continue;                       // comment

            if (line.StartsWith("event: "))
            {
                sseEvent = line["event: ".Length..].Trim();
                continue;
            }
            if (!line.StartsWith("data: ")) continue;

            var dataStr = line["data: ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(dataStr)) continue;
            JsonNode? node = JsonNode.Parse(dataStr);

            // Some servers repeat "type" inside the data; prefer explicit SSE event when present
            var type = sseEvent ?? node?["type"]?.GetValue<string>() ?? string.Empty;

            switch (type)
            {
                case "conversation.response.started":
                    // Optional: capture conversation_id if needed
                    // var convId = node?["conversation_id"]?.GetValue<string>();
                    break;

                case "message.output.delta":
                    {
                        modelId = node?["model"]?.GetValue<string>() ?? modelId;
                        streamId ??= node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

                        var contentNode = node?["content"];
                        if (contentNode is null) break;

                        if (contentNode.GetValueKind() == JsonValueKind.String)
                        {
                            var delta = contentNode.GetValue<string>();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                if (!textStarted)
                                {
                                    yield return streamId.ToTextStartUIMessageStreamPart();
                                    textStarted = true;
                                }

                                yield return new TextDeltaUIMessageStreamPart
                                {
                                    Id = streamId,
                                    Delta = delta
                                };
                            }
                        }
                        else if (contentNode.GetValueKind() == JsonValueKind.Object)
                        {
                            var contentType = contentNode?["type"]?.GetValue<string>();
                            if (contentType == "tool_reference")
                            {
                                var urlSource = contentNode?["url"]?.GetValue<string>();
                                var title = contentNode?["title"]?.GetValue<string>();

                                if (!string.IsNullOrWhiteSpace(urlSource) && urlSource.StartsWith("https://"))
                                {
                                    yield return new SourceUIPart
                                    {
                                        Url = urlSource,
                                        Title = string.IsNullOrWhiteSpace(title) ? null : title,
                                        SourceId = urlSource
                                    };
                                }
                            }
                            else if (contentType == "tool_file")
                            {
                                var fileId = contentNode?["file_id"]?.GetValue<string>();
                                var fileName = contentNode?["file_name"]?.GetValue<string>() ?? "file";
                                var fileExt = contentNode?["file_type"]?.GetValue<string>() ?? "";

                                if (!string.IsNullOrEmpty(fileId))
                                {
                                    // Fetch metadata: GET /v1/files/{file_id}
                                    using var r1 = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}");
                                    using var fileInfoResp = await _client.SendAsync(r1, cancellationToken);
                                    if (!fileInfoResp.IsSuccessStatusCode)
                                    {
                                        yield return $"Error retrieving file metadata: {fileId}"
                                            .ToErrorUIPart();
                                        break;

                                    }

                                    var infoStr = await fileInfoResp.Content.ReadAsStringAsync(cancellationToken);
                                    var infoNode = JsonNode.Parse(infoStr);
                                    var deleted = infoNode?["deleted"]?.GetValue<bool?>() ?? false;
                                    if (deleted)
                                    {
                                        yield return $"File deleted before retrieval: {fileId}"
                                            .ToErrorUIPart();
                                        break;
                                    }

                                    var mimetype = infoNode?["mimetype"]?.GetValue<string?>() ?? MediaTypeNames.Application.Octet;

                                    // Fetch content: GET /v1/files/{file_id}/content
                                    using var r2 = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{fileId}/content");
                                    using var fileContentResp = await _client.SendAsync(r2, cancellationToken);
                                    if (!fileContentResp.IsSuccessStatusCode)
                                    {
                                        yield return $"Error downloading file content: {fileId}"
                                            .ToErrorUIPart();
                                        break;
                                    }

                                    var bytes = await fileContentResp.Content.ReadAsByteArrayAsync(cancellationToken);

                                    yield return bytes.ToFileUIPart(mimetype);
                                }

                            }
                        }

                        break;
                    }


                case "conversation.response.done":
                    {
                        sawDone = true;

                        var usage = node?["usage"];
                        inputTokens = usage?["prompt_tokens"]?.GetValue<int?>() ?? inputTokens;
                        outputTokens = usage?["completion_tokens"]?.GetValue<int?>() ?? outputTokens;
                        totalTokens = usage?["total_tokens"]?.GetValue<int?>() ?? (inputTokens + outputTokens);

                        // Close any open text stream
                        if (textStarted && streamId is not null)
                        {
                            yield return new TextEndUIMessageStreamPart { Id = streamId };
                            textStarted = false;
                        }

                        if (toolStreamId != null && streamingToolName != string.Empty)
                        {

                            yield return new ToolCallPart()
                            {
                                ToolCallId = toolStreamId,
                                ToolName = streamingToolName,
                                Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == streamingToolName)?.Title,
                                Input = JsonSerializer.Deserialize<object>(toolInput ?? "{}")!,
                                ProviderExecuted = false
                            };

                            yield return new ToolApprovalRequestPart
                            {
                                ToolCallId = toolStreamId,
                                ApprovalId = Guid.NewGuid().ToString(),
                            };

                            toolStreamId = null;
                            streamingToolName = string.Empty;
                        }

                        // Finish (no explicit finish_reason in example; use "stop")
                        yield return "stop".ToFinishUIPart(
                            modelId,
                            outputTokens,
                            inputTokens,
                            totalTokens,
                            chatRequest.Temperature,
                            reasoningTokens: null,
                            extraMetadata: null
                        );
                        break;
                    }

                case "conversation.response.error":
                    {
                        var err = node?.ToJsonString();
                        if (textStarted && streamId is not null)
                        {
                            yield return new TextEndUIMessageStreamPart { Id = streamId };
                            textStarted = false;
                        }
                        yield return $"Mistral stream error event: {err}".ToErrorUIPart();
                        yield break;
                    }
                case "tool.execution.delta":
                    var toolDeltaId = node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var argStreamDelta = node?["arguments"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    toolInput += argStreamDelta;

                    yield return new ToolCallDeltaPart
                    {
                        ToolCallId = toolDeltaId,
                        InputTextDelta = argStreamDelta
                    };

                    break;

                case "tool.execution.started":
                    var toolId = node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var toolName = node?["name"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

                    if (!toolName.StartsWith("web_search"))
                    {
                        var argStream = node?["arguments"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

                        toolStreamId = toolId;
                        toolInput = argStream;

                        yield return new ToolCallStreamingStartPart
                        {
                            ToolCallId = toolId,
                            ToolName = toolName,
                            Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
                            ProviderExecuted = toolName == "code_interpreter"
                                || toolName == "image_generation"
                        };

                        yield return new ToolCallDeltaPart
                        {
                            ToolCallId = toolId,
                            InputTextDelta = argStream,

                        };
                    }
                    else
                    {
                        yield return new ToolCallPart
                        {
                            ToolCallId = toolId,
                            ToolName = toolName,
                            Input = new { },
                            ProviderExecuted = true
                        };
                    }
                    break;
                case "tool.execution.done":
                    var toolCallId = node?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var toolDoneName = node?["name"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

                    if (toolStreamId != null)
                    {
                        toolStreamId = null;

                        yield return new ToolCallPart()
                        {
                            ToolCallId = toolCallId,
                            ToolName = toolDoneName,
                            Input = JsonSerializer.Deserialize<object>(toolInput ?? "{}")!,
                            ProviderExecuted = true
                        };
                    }

                    yield return new ToolOutputAvailablePart()
                    {
                        ToolCallId = toolCallId,
                        Output = new { },
                        ProviderExecuted = true
                    };

                    break;

                case "agent.handoff.started":
                case "agent.handoff.done":
                case "function.call.delta":
                    var deltaId = node?["tool_call_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var argDelta = node?["arguments"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var argDeltaName = node?["name"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");

                    if (toolStreamId == null)
                    {
                        toolStreamId = deltaId;
                        streamingToolName = argDeltaName;

                        yield return new ToolCallStreamingStartPart
                        {
                            ToolCallId = deltaId,
                            ToolName = argDeltaName,
                            ProviderExecuted = false
                        };
                    }
                    else if (toolStreamId != deltaId)
                    {
                        yield return new ToolCallPart()
                        {
                            ToolCallId = toolStreamId,
                            ToolName = streamingToolName,
                            Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == streamingToolName)?.Title,
                            Input = JsonSerializer.Deserialize<object>(toolInput ?? "{}")!,
                            ProviderExecuted = false
                        };

                        yield return new ToolApprovalRequestPart
                        {
                            ToolCallId = toolStreamId,
                            ApprovalId = Guid.NewGuid().ToString(),
                        };

                        toolStreamId = deltaId;
                        streamingToolName = argDeltaName;
                        toolInput = string.Empty;

                        yield return new ToolCallStreamingStartPart
                        {
                            ToolCallId = deltaId,
                            ToolName = argDeltaName,
                            ProviderExecuted = false
                        };
                    }

                    toolInput += argDelta;
                    yield return new ToolCallDeltaPart
                    {
                        ToolCallId = deltaId,
                        InputTextDelta = argDelta,
                    };
                    break;
                default:
                    break;
            }
        }

        // Safety net if stream ended without explicit done
        if (!sawDone)
        {
            if (textStarted && streamId is not null)
                yield return new TextEndUIMessageStreamPart { Id = streamId };

            yield return "stop".ToFinishUIPart(
                modelId,
                outputTokens,
                inputTokens,
                inputTokens + outputTokens,
                chatRequest.Temperature,
                reasoningTokens: null,
                extraMetadata: null
            );
        }
    }
}
