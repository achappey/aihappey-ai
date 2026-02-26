using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = ToResponseRequest(options, stream: false);
        var response = await ResponsesAsyncInternal(request, cancellationToken).ConfigureAwait(false);

        var model = NormalizeIncomingModel(response.Model, options.Model);
        var text = ExtractAssistantText(response.Output);

        return new ChatCompletion
        {
            Id = string.IsNullOrWhiteSpace(response.Id) ? Guid.NewGuid().ToString("n") : response.Id,
            Created = response.CreatedAt,
            Model = model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = MapFinishReason(response.Status)
                }
            ],
            Usage = response.Usage
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = ToResponseRequest(options, stream: true);
        var responseId = $"vapi-chatcmpl-{Guid.NewGuid():N}";
        var model = NormalizeIncomingModel(request.Model, options.Model);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var roleSent = false;

        await foreach (var part in ResponsesStreamingAsyncInternal(request, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            switch (part)
            {
                case ResponseOutputTextDelta delta:
                    {
                        if (!roleSent)
                        {
                            roleSent = true;
                            yield return new ChatCompletionUpdate
                            {
                                Id = responseId,
                                Created = created,
                                Model = model,
                                Choices =
                                [
                                    new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                                ]
                            };
                        }

                        if (!string.IsNullOrEmpty(delta.Delta))
                        {
                            yield return new ChatCompletionUpdate
                            {
                                Id = responseId,
                                Created = created,
                                Model = model,
                                Choices =
                                [
                                    new { index = 0, delta = new { content = delta.Delta }, finish_reason = (string?)null }
                                ]
                            };
                        }

                        break;
                    }

                case ResponseCompleted completed:
                    {
                        var finalModel = NormalizeIncomingModel(completed.Response?.Model, model);
                        yield return new ChatCompletionUpdate
                        {
                            Id = responseId,
                            Created = created,
                            Model = finalModel,
                            Choices =
                            [
                                new { index = 0, delta = new { }, finish_reason = "stop" }
                            ],
                            Usage = completed.Response?.Usage
                        };
                        yield break;
                    }

                case ResponseFailed failed:
                    {
                        var finalModel = NormalizeIncomingModel(failed.Response?.Model, model);
                        yield return new ChatCompletionUpdate
                        {
                            Id = responseId,
                            Created = created,
                            Model = finalModel,
                            Choices =
                            [
                                new { index = 0, delta = new { }, finish_reason = "error" }
                            ],
                            Usage = failed.Response?.Usage
                        };
                        yield break;
                    }

                case ResponseError:
                    {
                        yield return new ChatCompletionUpdate
                        {
                            Id = responseId,
                            Created = created,
                            Model = model,
                            Choices =
                            [
                                new { index = 0, delta = new { }, finish_reason = "error" }
                            ]
                        };
                        yield break;
                    }
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = responseId,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ]
        };
    }

    private static ResponseRequest ToResponseRequest(ChatCompletionOptions options, bool stream)
    {
        var inputItems = new List<ResponseInputItem>();

        foreach (var message in options.Messages ?? [])
        {
            var text = ChatMessageContentExtensions.ToText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            inputItems.Add(new ResponseInputMessage
            {
                Role = (message.Role ?? string.Empty).ToLowerInvariant() switch
                {
                    "system" => ResponseRole.System,
                    "assistant" => ResponseRole.Assistant,
                    "developer" => ResponseRole.Developer,
                    _ => ResponseRole.User
                },
                Content = text
            });
        }

        return new ResponseRequest
        {
            Model = options.Model,
            Input = inputItems,
            Stream = stream,
            Temperature = options.Temperature
        };
    }

    private static string ExtractAssistantText(IEnumerable<object>? output)
    {
        if (output is null)
            return string.Empty;

        var chunks = new List<string>();

        foreach (var item in output)
        {
            if (item is not JsonElement el || el.ValueKind != JsonValueKind.Object)
                continue;

            if (!el.TryGetProperty("role", out var roleEl)
                || !string.Equals(roleEl.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!el.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var c in contentEl.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object)
                    continue;

                if (c.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    chunks.Add(textEl.GetString() ?? string.Empty);
            }
        }

        return string.Concat(chunks);
    }

    private static string MapFinishReason(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop";
}

