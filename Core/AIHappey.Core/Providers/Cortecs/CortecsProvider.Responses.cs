using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = MapResponsesRequestToNative(options, stream: false);
        var native = await SendNativeAsync(payload, cancellationToken);
        return MapNativeToResponseResult(native, options);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = MapResponsesRequestToNative(options, stream: true);

        var responseId = Guid.NewGuid().ToString("n");
        var model = options.Model ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 0;
        var outputIndex = 0;
        var contentIndex = 0;
        var itemId = $"msg_{responseId}";
        var fullText = new System.Text.StringBuilder();
        string? lastFinishReason = null;
        object? finalUsage = null;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Object = "response",
                CreatedAt = createdAt,
                Status = "in_progress",
                Model = model,
                Temperature = options.Temperature,
                ParallelToolCalls = options.ParallelToolCalls,
                MaxOutputTokens = options.MaxOutputTokens,
                Store = options.Store,
                ToolChoice = options.ToolChoice,
                Tools = options.Tools?.Cast<object>() ?? [],
                Text = options.Text,
                Metadata = options.Metadata,
                Output = []
            }
        };

        await foreach (var native in SendNativeStreamingAsync(payload, cancellationToken))
        {
            var update = MapNativeToChatCompletionUpdate(native, options.Model ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(update.Id))
                responseId = update.Id;

            if (!string.IsNullOrWhiteSpace(update.Model))
                model = update.Model;

            var deltaText = TryGetDeltaText(update);
            if (!string.IsNullOrWhiteSpace(deltaText))
            {
                fullText.Append(deltaText);
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = outputIndex,
                    ContentIndex = contentIndex,
                    Delta = deltaText
                };
            }

            var finishReason = TryGetFinishReason(update);
            if (!string.IsNullOrWhiteSpace(finishReason))
                lastFinishReason = finishReason;

            if (update.Usage is not null)
                finalUsage = update.Usage;
        }

        var doneText = fullText.ToString();
        if (!string.IsNullOrWhiteSpace(doneText))
        {
            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = outputIndex,
                ContentIndex = contentIndex,
                Text = doneText
            };
        }

        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completed = string.IsNullOrWhiteSpace(lastFinishReason)
            || string.Equals(lastFinishReason, "stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lastFinishReason, "tool_calls", StringComparison.OrdinalIgnoreCase);

        var response = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = completed ? "completed" : "failed",
            Model = model,
            Temperature = options.Temperature,
            ParallelToolCalls = options.ParallelToolCalls,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            Metadata = options.Metadata,
            Usage = finalUsage,
            Error = completed
                ? null
                : new ResponseResultError
                {
                    Code = lastFinishReason,
                    Message = $"Chat completion finished with reason '{lastFinishReason}'."
                },
            Output =
            [
                new
                {
                    id = itemId,
                    type = "message",
                    status = completed ? "completed" : "failed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = doneText
                        }
                    }
                }
            ]
        };

        if (completed)
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence++,
                Response = response
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence++,
                Response = response
            };
        }
    }

    private static object MapResponsesRequestToNative(ResponseRequest options, bool stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);

        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(options.Instructions))
        {
            messages.Add(new
            {
                role = "system",
                content = options.Instructions
            });
        }

        if (options.Input?.IsText == true && !string.IsNullOrWhiteSpace(options.Input.Text))
        {
            messages.Add(new
            {
                role = "user",
                content = options.Input.Text
            });
        }

        if (options.Input?.IsItems == true && options.Input.Items is not null)
        {
            foreach (var item in options.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role switch
                {
                    ResponseRole.System => "system",
                    ResponseRole.Developer => "system",
                    ResponseRole.Assistant => "assistant",
                    _ => "user"
                };

                if (message.Content.IsText && !string.IsNullOrWhiteSpace(message.Content.Text))
                {
                    messages.Add(new
                    {
                        role,
                        content = message.Content.Text
                    });
                    continue;
                }

                if (!message.Content.IsParts || message.Content.Parts is null)
                    continue;

                var parts = new List<object>();
                foreach (var part in message.Content.Parts)
                {
                    switch (part)
                    {
                        case InputTextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                            parts.Add(new { type = "text", text = textPart.Text });
                            break;
                        case InputImagePart imagePart when !string.IsNullOrWhiteSpace(imagePart.ImageUrl):
                            parts.Add(new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = imagePart.ImageUrl,
                                    detail = imagePart.Detail
                                }
                            });
                            break;
                        case InputFilePart filePart when !string.IsNullOrWhiteSpace(filePart.FileData):
                            parts.Add(new
                            {
                                type = "input_file",
                                file_data = filePart.FileData,
                                filename = filePart.Filename,
                                file_id = filePart.FileId
                            });
                            break;
                    }
                }

                if (parts.Count == 0)
                    continue;

                messages.Add(new
                {
                    role,
                    content = parts
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["max_tokens"] = options.MaxOutputTokens,
            ["response_format"] = options.Text,
            ["tools"] = options.Tools?.Any() == true ? options.Tools.ToCortecsTools().ToList() : null,
            ["tool_choice"] = options.ToolChoice,
            ["parallel_tool_calls"] = options.ParallelToolCalls
        };

        var root = JsonSerializer.SerializeToElement(options, ResponseJson.Default);
        AddIfPresent(root, payload, "preference");
        AddIfPresent(root, payload, "allowed_providers");
        AddIfPresent(root, payload, "eu_native");
        AddIfPresent(root, payload, "allow_quantization");
        AddIfPresent(root, payload, "frequency_penalty");
        AddIfPresent(root, payload, "presence_penalty");
        AddIfPresent(root, payload, "stop");
        AddIfPresent(root, payload, "logprobs");
        AddIfPresent(root, payload, "seed");
        AddIfPresent(root, payload, "n");
        AddIfPresent(root, payload, "prediction");
        AddIfPresent(root, payload, "safe_prompt");

        return payload;
    }

    private static ResponseResult MapNativeToResponseResult(JsonElement native, ResponseRequest request)
    {
        var completion = MapNativeToChatCompletion(native, request.Model ?? string.Empty);
        var text = ExtractAssistantTextFromChoices(completion.Choices);
        var finishReason = ExtractFinishReason(completion.Choices) ?? "stop";
        var completed = string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase);

        return new ResponseResult
        {
            Id = completion.Id,
            Object = "response",
            CreatedAt = completion.Created,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = completed ? "completed" : "failed",
            Model = completion.Model,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            Metadata = request.Metadata,
            Usage = completion.Usage,
            Error = completed
                ? null
                : new ResponseResultError
                {
                    Code = finishReason,
                    Message = $"Chat completion finished with reason '{finishReason}'."
                },
            Output =
            [
                new
                {
                    id = $"msg_{completion.Id}",
                    type = "message",
                    status = completed ? "completed" : "failed",
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

    private static string ExtractAssistantTextFromChoices(IEnumerable<object>? choices)
    {
        if (choices is null)
            return string.Empty;

        var root = JsonSerializer.SerializeToElement(choices, JsonSerializerOptions.Web);
        foreach (var choice in root.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            if (!message.TryGetProperty("content", out var content))
                continue;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        var value = part.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            parts.Add(value);
                        continue;
                    }

                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            parts.Add(value);
                    }
                }

                if (parts.Count > 0)
                    return string.Concat(parts);
            }
        }

        return string.Empty;
    }

    private static string? ExtractFinishReason(IEnumerable<object>? choices)
    {
        if (choices is null)
            return null;

        var root = JsonSerializer.SerializeToElement(choices, JsonSerializerOptions.Web);
        foreach (var choice in root.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String)
            {
                return finishReason.GetString();
            }
        }

        return null;
    }

    private static string? TryGetDeltaText(ChatCompletionUpdate update)
    {
        var root = JsonSerializer.SerializeToElement(update.Choices ?? [], JsonSerializerOptions.Web);
        foreach (var choice in root.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            if (!delta.TryGetProperty("content", out var content))
                continue;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        var value = part.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            parts.Add(value);
                    }
                }

                if (parts.Count > 0)
                    return string.Concat(parts);
            }
        }

        return null;
    }

    private static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        var root = JsonSerializer.SerializeToElement(update.Choices ?? Array.Empty<object>(), JsonSerializerOptions.Web);
        foreach (var choice in root.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String)
            {
                return finishReason.GetString();
            }
        }

        return null;
    }
}

