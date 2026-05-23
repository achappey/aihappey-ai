using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.StealthGPT;

public partial class StealthGPTProvider
{
    private static readonly JsonSerializerOptions StealthGptJson = JsonSerializerOptions.Web;

    private static string BuildPromptFromUnifiedRequest(AIRequest request)
    {
        var instructionSections = new List<string>();
        var conversationSections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructionSections.Add(request.Instructions.Trim());

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            conversationSections.Add(FormatConversationBlock("user", request.Input.Text!));

        foreach (var item in request.Input?.Items ?? [])
        {
            ValidateSupportedInputItem(item);

            var text = ExtractSupportedText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var role = (item.Role ?? "user").Trim().ToLowerInvariant();

            switch (role)
            {
                case "system":
                case "developer":
                    instructionSections.Add(text);
                    break;
                case "user":
                case "assistant":
                    conversationSections.Add(FormatConversationBlock(role, text));
                    break;
                default:
                    throw new NotSupportedException($"StealthGPT unified mode only supports system, developer, user, and assistant message roles. Role '{item.Role}' is not supported.");
            }
        }

        var sections = new List<string>();

        if (instructionSections.Count > 0)
            sections.Add("instructions:\n" + string.Join("\n\n", instructionSections));

        sections.AddRange(conversationSections);

        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    private static string FormatConversationBlock(string role, string text)
        => $"{role}: {text.Trim()}";

    private static void ValidateSupportedInputItem(AIInputItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Type)
            && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"StealthGPT unified mode only supports message input items. Item type '{item.Type}' is not supported.");
        }
    }

    private static string ExtractSupportedText(List<AIContentPart>? content)
    {
        if (content is null || content.Count == 0)
            return string.Empty;

        var textParts = new List<string>();

        foreach (var part in content)
        {
            switch (part)
            {
                case AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    textParts.Add(textPart.Text.Trim());
                    break;
                case AIReasoningContentPart:
                    break;
                case AIFileContentPart:
                    throw new NotSupportedException("StealthGPT unified mode does not support file or image content parts.");
                case AIToolCallContentPart:
                    throw new NotSupportedException("StealthGPT unified mode does not support tool call content parts.");
                case null:
                    break;
                default:
                    throw new NotSupportedException($"StealthGPT unified mode does not support content part type '{part.Type}'.");
            }
        }

        return string.Join("\n\n", textParts);
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

    private static Dictionary<string, object>? ToProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in metadata)
        {
            if (item.Value is not null)
                result[item.Key] = item.Value;
        }

        return result.Count == 0 ? null : result;
    }

    private void ValidateUnifiedRequest(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("StealthGPT unified mode does not support tool definitions.");

        if (request.ResponseFormat is not null)
            throw new NotSupportedException("StealthGPT unified mode does not support structured response formats.");
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

        ApplyAuthHeader();

        var providerMetadata = TryExtractProviderMetadata(metadata);
        var isArticles = IsArticlesModel(model);

        var endpoint = isArticles ? "stealthify/articles" : "stealthify";
        object requestBody;
        object responseBody;
        string output;
        Dictionary<string, object?> usage;

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
            usage = new Dictionary<string, object?>
            {
                ["remaining_credits"] = response.RemainingCredits
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
                Model = providerMetadata?.Model ?? "heavy",
                Business = providerMetadata?.Business,
                IsMultilingual = providerMetadata?.IsMultilingual,
                Detector = providerMetadata?.Detector,
                OutputFormat = providerMetadata?.OutputFormat ?? "text"
            };

            requestBody = request;
            var response = await PostAsJsonAsync<StealthGptStealthifyRequest, StealthGptStealthifyResponse>(endpoint, request, cancellationToken);
            responseBody = response;
            output = response.Result ?? string.Empty;
            usage = new Dictionary<string, object?>
            {
                ["words_spent"] = response.WordsSpent,
                ["remaining_credits"] = response.RemainingCredits,
                ["billing_mode"] = response.BillingMode,
                ["metered_charged_credits"] = response.MeteredChargedCredits,
                ["tokens_spent"] = response.TokensSpent,
                ["total_tokens_spent"] = response.TotalTokensSpent,
                ["system_tokens_spent"] = response.SystemTokensSpent,
                ["how_likely_to_be_detected"] = response.HowLikelyToBeDetected
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

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateUnifiedRequest(request);

        var modelId = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromUnifiedRequest(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(request));

        var native = await ExecuteNativeTextAsync(modelId, prompt, request.Metadata, cancellationToken);

        return CreateUnifiedResponse(request, native);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateUnifiedRequest(request);
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault(textPart => !string.IsNullOrEmpty(textPart))
            ?? string.Empty;

        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var metadata = response.Metadata;
        var timestamp = DateTimeOffset.UtcNow;
        var providerMetadata = ToProviderMetadata(metadata);

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-start",
            new AITextStartEventData
            {
                ProviderMetadata = providerMetadata
            },
            timestamp,
            metadata);

        foreach (var delta in ChunkText(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = delta,
                    ProviderMetadata = providerMetadata
                },
                timestamp,
                metadata);
        }

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-end",
            new AITextEndEventData
            {
                ProviderMetadata = providerMetadata
            },
            timestamp,
            metadata);

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model?.ToModelId(GetIdentifier()),
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? string.Empty,
                        timestamp,
                        usage: response.Usage,
                        additionalProperties: metadata)
                }
            },
            Metadata = metadata
        };
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, StealthGptNativeResult native)
        => new()
        {
            ProviderId = GetIdentifier(),
            Model = native.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new()
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = native.OutputText,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["stealthgpt.endpoint"] = native.Endpoint
                                }
                            }
                        ]
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["stealthgpt.endpoint"] = native.Endpoint
                }
            },
            Usage = native.Usage,
            Metadata = new Dictionary<string, object?>(native.ProviderMetadata)
            {
                ["model"] = native.Model,
                ["usage"] = native.Usage,
                ["stealthgpt.endpoint"] = native.Endpoint,
                ["stealthgpt.requested_model"] = request.Model
            }
        };

    private static AIStreamEvent CreateStreamEvent(
        string providerId,
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);

        return result.ToChatCompletion();
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToChatCompletionUpdate();
        }
    }
}
