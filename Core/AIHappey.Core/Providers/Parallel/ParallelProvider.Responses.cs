using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: false);
        var completion = await CompleteChatInternalAsync(completionOptions, cancellationToken);

        var text = ExtractAssistantTextFromChoices(completion.Choices);
        var createdAt = completion.Created > 0 ? completion.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = completion.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = "completed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = completion.Model,
            Temperature = options.Temperature,
            Usage = completion.Usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output =
            [
                new
                {
                    id = $"msg_{completion.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: true);
        var itemId = $"msg_{Guid.NewGuid():N}";

        var responseId = Guid.NewGuid().ToString("N");
        var model = completionOptions.Model;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;
        var text = new System.Text.StringBuilder();
        object? usage = null;
        bool hasAnyChunk = false;

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = model,
            Temperature = options.Temperature,
            Usage = null,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        await foreach (var rawChunk in StreamChatRawChunksAsync(completionOptions, cancellationToken))
        {
            hasAnyChunk = true;
            using var doc = System.Text.Json.JsonDocument.Parse(rawChunk);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                responseId = idEl.GetString() ?? responseId;

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == System.Text.Json.JsonValueKind.String)
                model = modelEl.GetString() ?? model;

            if (root.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                createdAt = createdEl.GetInt64();

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind is not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.Undefined)
                usage = System.Text.Json.JsonSerializer.Deserialize<object>(usageEl.GetRawText());

            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var choice in choicesEl.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var delta = contentEl.GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        text.Append(delta);
                        yield return new ResponseOutputTextDelta
                        {
                            SequenceNumber = sequence++,
                            ItemId = itemId,
                            ContentIndex = 0,
                            Outputindex = 0,
                            Delta = delta
                        };
                    }
                }
            }
        }

        var finalText = text.ToString();

        if (!string.IsNullOrEmpty(finalText))
        {
            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Text = finalText
            };
        }

        var result = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = hasAnyChunk ? "completed" : "failed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = model,
            Temperature = options.Temperature,
            Usage = usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Error = hasAnyChunk
                ? null
                : new ResponseResultError
                {
                    Code = "parallel_empty_stream",
                    Message = "Parallel stream ended without any completion chunks."
                },
            Output =
            [
                new
                {
                    id = itemId,
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = finalText
                        }
                    }
                }
            ]
        };

        if (hasAnyChunk)
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = result
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = result
            };
        }
    }

    private ChatCompletionOptions BuildChatOptionsFromResponseRequest(ResponseRequest request, bool stream)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = JsonSerializer.SerializeToElement(request.Instructions, Json)
            });
        }

        if (request.Input?.IsText == true)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(request.Input.Text ?? string.Empty, Json)
            });
        }

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role switch
                {
                    ResponseRole.System => "system",
                    ResponseRole.Assistant => "assistant",
                    ResponseRole.Developer => "system",
                    _ => "user"
                };

                var text = FlattenResponseMessageContent(message.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                messages.Add(new ChatMessage
                {
                    Role = role,
                    Content = JsonSerializer.SerializeToElement(text, Json)
                });
            }
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(string.Empty, Json)
            });
        }

        return new ChatCompletionOptions
        {
            Model = request.Model ?? throw new ArgumentException("Model is required.", nameof(request)),
            Temperature = request.Temperature,
            Stream = stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ResponseFormat = request.Text,
            ToolChoice = request.ToolChoice as string,
            Tools = request.Tools?.Select(ToChatTool).ToArray() ?? [],
            Messages = messages
        };
    }

    private static object ToChatTool(ResponseToolDefinition tool)
    {
        if (!string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase))
            return new { type = tool.Type };

        string? name = null;
        string? description = null;
        object? parameters = null;

        if (tool.Extra is not null)
        {
            if (tool.Extra.TryGetValue("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString();

            if (tool.Extra.TryGetValue("description", out var d) && d.ValueKind == JsonValueKind.String)
                description = d.GetString();

            if (tool.Extra.TryGetValue("parameters", out var p)
                && p.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parameters = JsonSerializer.Deserialize<object>(p.GetRawText(), Json);
            }
        }

        name ??= "function";
        parameters ??= new { type = "object", properties = new { } };

        return new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters
            }
        };
    }

    private static string FlattenResponseMessageContent(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text ?? string.Empty;

        if (!content.IsParts || content.Parts is null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var part in content.Parts)
        {
            switch (part)
            {
                case InputTextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    parts.Add(textPart.Text);
                    break;
                case InputImagePart imagePart:
                    parts.Add(imagePart.ImageUrl ?? imagePart.FileId ?? "[input_image]");
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(part, Json));
                    break;
            }
        }

        return string.Join("\n", parts.Where(a => !string.IsNullOrWhiteSpace(a)));
    }
}

