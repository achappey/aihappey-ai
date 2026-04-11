using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.BotVerse;

public partial class BotVerseProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateUnifiedRequest(request);

        var prompt = BuildPrompt(request);
        var result = await GenerateAsync(request, prompt, cancellationToken);

        return CreateUnifiedResponse(request, result);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateUnifiedRequest(request);

        var prompt = BuildPrompt(request);
        var result = await GenerateAsync(request, prompt, cancellationToken);
        var response = CreateUnifiedResponse(request, result);
        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var metadata = response.Metadata;
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-start",
            new AITextStartEventData(),
            timestamp,
            metadata);

        foreach (var delta in ChunkText(result.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = delta
                },
                timestamp,
                metadata);
        }

        yield return CreateStreamEvent(
            providerId,
            eventId,
            "text-end",
            new AITextEndEventData(),
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
                    Model = response.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    OutputTokens = result.TokensUsed,
                    TotalTokens = result.TokensUsed,
                    MessageMetadata = ToMessageMetadata(metadata)
                }
            },
            Metadata = metadata
        };
    }

    private async Task<BotVerseGenerateResult> GenerateAsync(
        AIRequest request,
        string prompt,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/agents/me/generate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new BotVerseGenerateRequest
                    {
                        Prompt = prompt,
                        MaxTokens = request.MaxOutputTokens
                    },
                    JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"BotVerse generate error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

        var payload = JsonSerializer.Deserialize<BotVerseGenerateResponse>(raw, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("BotVerse returned an empty response.");

        return new BotVerseGenerateResult
        {
            Text = payload.Text ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(payload.Model) ? request.Model : payload.Model,
            TokensUsed = payload.TokensUsed,
            RemainingToday = payload.RemainingToday
        };
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, BotVerseGenerateResult result)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["botverse.tokens_used"] = result.TokensUsed,
            ["botverse.remaining_today"] = result.RemainingToday
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
            metadata["botverse.requested_model"] = request.Model;

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = result.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items = new List<AIOutputItem>
                {
                    new()
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = new List<AIContentPart>
                        {
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = result.Text
                            }
                        }
                    }
                }
            },
            Usage = new Dictionary<string, object?>
            {
                ["output_tokens"] = result.TokensUsed,
                ["total_tokens"] = result.TokensUsed
            },
            Metadata = metadata
        };
    }

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

    private static IEnumerable<string> ChunkText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        const int chunkSize = 256;

        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var length = Math.Min(chunkSize, text.Length - i);
            yield return text.Substring(i, length);
        }
    }

    private static void ValidateUnifiedRequest(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("BotVerse unified mode does not support tool definitions.");

        if (request.ToolChoice is not null)
            throw new NotSupportedException("BotVerse unified mode does not support tool choice.");

        if (request.ResponseFormat is not null)
            throw new NotSupportedException("BotVerse unified mode does not support structured response formats.");
    }

    private static string BuildPrompt(AIRequest request)
    {
        var instructionSections = new List<string>();
        var conversationSections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructionSections.Add(request.Instructions.Trim());

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            conversationSections.Add(FormatConversationBlock("user", request.Input.Text!));

        if (request.Input?.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                cancellationSafeValidate(item);

                var text = ExtractSupportedText(item.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var role = (item.Role ?? "user").Trim().ToLowerInvariant();

                switch (role)
                {
                    case "system":
                        instructionSections.Add(text);
                        break;
                    case "user":
                    case "assistant":
                        conversationSections.Add(FormatConversationBlock(role, text));
                        break;
                    default:
                        throw new NotSupportedException($"BotVerse unified mode only supports system, user, and assistant message roles. Role '{item.Role}' is not supported.");
                }
            }
        }

        var sections = new List<string>();

        if (instructionSections.Count > 0)
            sections.Add("instructions:\n" + string.Join("\n\n", instructionSections));

        sections.AddRange(conversationSections);

        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));

        static void cancellationSafeValidate(AIInputItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Type)
                && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"BotVerse unified mode only supports message input items. Item type '{item.Type}' is not supported.");
            }
        }
    }

    private static string FormatConversationBlock(string role, string text)
        => $"{role}: {text.Trim()}";

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
                    throw new NotSupportedException("BotVerse unified mode does not support file or image content parts.");
                case AIToolCallContentPart:
                    throw new NotSupportedException("BotVerse unified mode does not support tool call content parts.");
                case null:
                    break;
                default:
                    throw new NotSupportedException($"BotVerse unified mode does not support content part type '{part.Type}'.");
            }
        }

        return string.Join("\n\n", textParts);
    }

    private static string ExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown BotVerse error.";

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString() ?? raw;
            }
        }
        catch
        {
            // Ignore JSON parse failures and fall back to the raw response.
        }

        return raw;
    }

    private static Dictionary<string, object>? ToMessageMetadata(Dictionary<string, object?>? metadata)
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

    private sealed class BotVerseGenerateRequest
    {
        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }
    }

    private sealed class BotVerseGenerateResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("tokens_used")]
        public int? TokensUsed { get; init; }

        [JsonPropertyName("remaining_today")]
        public int? RemainingToday { get; init; }
    }

    private sealed class BotVerseGenerateResult
    {
        public required string Text { get; init; }

        public string? Model { get; init; }

        public int? TokensUsed { get; init; }

        public int? RemainingToday { get; init; }
    }
}
