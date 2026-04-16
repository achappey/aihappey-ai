using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StealthGPT;

public partial class StealthGPTProvider
{
    private static readonly JsonSerializerOptions StealthGptJson = JsonSerializerOptions.Web;

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var msg in messages ?? [])
        {
            var text = ChatMessageContentExtensions.ToText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{msg.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = string.Join("\n", message.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsText == true)
            return request.Input.Text ?? string.Empty;

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            var lines = new List<string>();
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role.ToString().ToLowerInvariant();
                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{role}: {text}");
            }

            if (lines.Count > 0)
                return string.Join("\n\n", lines);
        }

        return request.Instructions ?? string.Empty;
    }

    private static bool IsArticlesModel(string model)
        => model.Trim().EndsWith("/articles", StringComparison.OrdinalIgnoreCase);

    private static StealthGptProviderMetadata? TryExtractProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(nameof(StealthGPT).ToLowerInvariant(), out var raw) || raw is null)
            return null;

        try
        {
            var element = JsonSerializer.SerializeToElement(raw, StealthGptJson);
            return element.ValueKind == JsonValueKind.Object
                ? element.Deserialize<StealthGptProviderMetadata>(StealthGptJson)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> BuildMappedMetadata(
        Dictionary<string, object?>? metadata,
        object requestBody,
        object responseBody,
        string endpoint)
    {
        var mapped = metadata is null
            ? []
            : new Dictionary<string, object?>(metadata);

        mapped[nameof(StealthGPT).ToLowerInvariant()] = new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["request"] = requestBody,
            ["response"] = responseBody
        };

        return mapped;
    }

    private static IEnumerable<string> ChunkText(string text, int maxChunkLength = 120)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var index = 0;
        while (index < text.Length)
        {
            var length = Math.Min(maxChunkLength, text.Length - index);
            var end = index + length;

            if (end < text.Length)
            {
                var lastBreak = text.LastIndexOfAny([' ', '\n', '\r', '\t'], end - 1, length);
                if (lastBreak > index)
                    end = lastBreak + 1;
            }

            if (end <= index)
                end = Math.Min(index + maxChunkLength, text.Length);

            yield return text[index..end];
            index = end;
        }
    }

    private async Task<StealthGptNativeResult> ExecuteNativeTextAsync(
        string model,
        string prompt,
        Dictionary<string, object?>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(prompt));

        var providerMetadata = TryExtractProviderMetadata(metadata);
        var isArticles = IsArticlesModel(model);

        var endpoint = isArticles ? "stealthify/articles" : "stealthify";
        object requestBody;
        object responseBody;
        string output;
        object usage;

        if (isArticles)
        {
            var request = new StealthGptArticlesRequest
            {
                Prompt = prompt,
                WithImages = providerMetadata?.WithImages ?? true,
                Size = providerMetadata?.Size ?? "small",
                OutputFormat = providerMetadata?.OutputFormat ?? "markdown"
            };

            requestBody = request;
            var response = await PostAsJsonAsync<StealthGptArticlesRequest, StealthGptArticlesResponse>(endpoint, request, cancellationToken);
            responseBody = response;
            output = response.Result ?? string.Empty;
            usage = new
            {
                remaining_credits = response.RemainingCredits
            };
        }
        else
        {
            var request = new StealthGptStealthifyRequest
            {
                Prompt = prompt,
                Rephrase = providerMetadata?.Rephrase ?? false,
                Tone = providerMetadata?.Tone,
                Mode = providerMetadata?.Mode,
                QualityMode = providerMetadata?.QualityMode ?? "quality",
                Business = providerMetadata?.Business,
                IsMultilingual = providerMetadata?.IsMultilingual,
                Detector = providerMetadata?.Detector,
                OutputFormat = providerMetadata?.OutputFormat ?? "text"
            };

            requestBody = request;
            var response = await PostAsJsonAsync<StealthGptStealthifyRequest, StealthGptStealthifyResponse>(endpoint, request, cancellationToken);
            responseBody = response;
            output = response.Result ?? string.Empty;
            usage = new
            {
                words_spent = response.WordsSpent,
                remaining_credits = response.RemainingCredits,
                billing_mode = response.BillingMode,
                metered_charged_credits = response.MeteredChargedCredits,
                tokens_spent = response.TokensSpent,
                total_tokens_spent = response.TotalTokensSpent,
                system_tokens_spent = response.SystemTokensSpent,
                how_likely_to_be_detected = response.HowLikelyToBeDetected
            };
        }

        return new StealthGptNativeResult
        {
            Model = model,
            Endpoint = endpoint,
            OutputText = output,
            RequestBody = requestBody,
            ResponseBody = responseBody,
            Usage = usage,
            ProviderMetadata = BuildMappedMetadata(metadata, requestBody, responseBody, endpoint)
        };
    }

    private async Task<TResponse> PostAsJsonAsync<TRequest, TResponse>(
        string endpoint,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, StealthGptJson), Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"StealthGPT error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

        return JsonSerializer.Deserialize<TResponse>(raw, StealthGptJson)
               ?? throw new InvalidOperationException("StealthGPT returned an empty payload.");
    }

    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var native = await ExecuteNativeTextAsync(modelId, prompt, metadata: null, cancellationToken);

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = native.OutputText },
                    finish_reason = "stop"
                }
            ],
            Usage = native.Usage
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var native = await ExecuteNativeTextAsync(modelId, prompt, metadata: null, cancellationToken);
        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ]
        };

        foreach (var chunk in ChunkText(native.OutputText))
        {
            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = modelId,
                Choices =
                [
                    new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ],
            Usage = native.Usage
        };
    }

    private async Task<ResponseResult> ResponsesInternalAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var native = await ExecuteNativeTextAsync(modelId, prompt, options.Metadata, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            CreatedAt = now,
            CompletedAt = now,
            Status = "completed",
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            ParallelToolCalls = options.ParallelToolCalls,
            Metadata = native.ProviderMetadata,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools ?? [],
            Text = options.Text,
            Usage = native.Usage,
            Output =
            [
                new
                {
                    type = "message",
                    id = Guid.NewGuid().ToString("n"),
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = native.OutputText,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingInternalAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var native = await ExecuteNativeTextAsync(modelId, prompt, options.Metadata, cancellationToken);
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 0;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = created,
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                ParallelToolCalls = options.ParallelToolCalls,
                Metadata = native.ProviderMetadata,
                ToolChoice = options.ToolChoice,
                Tools = options.Tools ?? [],
                Text = options.Text,
                Output = []
            }
        };

        foreach (var chunk in ChunkText(native.OutputText))
        {
            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Delta = chunk
            };
        }

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Text = native.OutputText
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = created,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Status = "completed",
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                ParallelToolCalls = options.ParallelToolCalls,
                Metadata = native.ProviderMetadata,
                ToolChoice = options.ToolChoice,
                Tools = options.Tools ?? [],
                Text = options.Text,
                Usage = native.Usage,
                Output =
                [
                    new
                    {
                        type = "message",
                        id = itemId,
                        status = "completed",
                        role = "assistant",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = native.OutputText,
                                annotations = Array.Empty<string>()
                            }
                        }
                    }
                ]
            }
        };
    }
}
