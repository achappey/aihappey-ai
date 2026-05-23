using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.TigerCity;

public partial class TigerCityProvider
{
    private static readonly JsonSerializerOptions TigerCityJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string TigerCityInfoToolName = "tigercity_info";

    private const string TigerCityInfoToolTitle = "TigerCity status update";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tigerRequest = CreateTigerCityRequest(request, stream: false);

        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate_response")
        {
            Content = CreateJsonContent(tigerRequest)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateTigerCityException(response, raw);

        var payload = JsonSerializer.Deserialize<TigerCityGenerateResponse>(raw, TigerCityJsonOptions)
            ?? throw new InvalidOperationException("TigerCity returned an empty response.");

        return CreateUnifiedResponse(request, payload);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tigerRequest = CreateTigerCityRequest(request, stream: true);

        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate_response")
        {
            Content = CreateJsonContent(tigerRequest)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateTigerCityException(response, raw);
        }

        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = CreateBaseMetadata(request, null, null);
        var textStarted = false;
        var accumulatedText = new StringBuilder();
        TigerCityUsage? usage = null;
        string? stopReason = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            TigerCityStreamChunk? chunk;

            string? parseError = null;

            try
            {
                chunk = JsonSerializer.Deserialize<TigerCityStreamChunk>(line, TigerCityJsonOptions);
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
                chunk = null;
            }

            if (parseError is not null)
            {
                yield return CreateStreamEvent(
                    providerId,
                    eventId,
                    "error",
                    new AIErrorEventData { ErrorText = $"TigerCity returned an invalid stream chunk: {parseError}" },
                    DateTimeOffset.UtcNow,
                    metadata);
                yield break;
            }

            if (chunk is null || string.IsNullOrWhiteSpace(chunk.Type))
                continue;

            switch (chunk.Type.ToLowerInvariant())
            {
                case "info":
                    foreach (var infoEvent in CreateInfoToolEvents(providerId, eventId, chunk.Message, DateTimeOffset.UtcNow, metadata))
                        yield return infoEvent;
                    break;

                case "delta":
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateStreamEvent(
                            providerId,
                            eventId,
                            "text-start",
                            new AITextStartEventData(),
                            DateTimeOffset.UtcNow,
                            metadata);
                    }

                    var delta = chunk.Delta ?? string.Empty;
                    accumulatedText.Append(delta);

                    if (delta.Length > 0)
                    {
                        yield return CreateStreamEvent(
                            providerId,
                            eventId,
                            "text-delta",
                            new AITextDeltaEventData { Delta = delta },
                            DateTimeOffset.UtcNow,
                            metadata);
                    }
                    break;

                case "stop":
                    usage = chunk.Usage;
                    stopReason = string.IsNullOrWhiteSpace(chunk.StopReason) ? "stop" : chunk.StopReason;
                    break;

                case "error":
                    yield return CreateStreamEvent(
                        providerId,
                        eventId,
                        "error",
                        new AIErrorEventData { ErrorText = chunk.Message ?? "TigerCity stream error." },
                        DateTimeOffset.UtcNow,
                        metadata);
                    yield break;
            }
        }

        var completedAt = DateTimeOffset.UtcNow;

        if (textStarted)
        {
            yield return CreateStreamEvent(
                providerId,
                eventId,
                "text-end",
                new AITextEndEventData(),
                completedAt,
                metadata);
        }

        var text = accumulatedText.ToString();
        var output = CreateOutput(text);
        metadata = CreateBaseMetadata(request, eventId, usage);

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = completedAt,
                Output = output,
                Data = new AIFinishEventData
                {
                    FinishReason = stopReason ?? "stop",
                    Model = request.Model?.ToModelId(GetIdentifier()),
                    CompletedAt = completedAt.ToUnixTimeSeconds(),
                    InputTokens = usage?.InputTokens,
                    OutputTokens = usage?.OutputTokens,
                    TotalTokens = CalculateTotalTokens(usage),
                    MessageMetadata = CreateFinishMetadata(request, usage, completedAt, metadata)
                }
            },
            Metadata = metadata
        };
    }

    private static StringContent CreateJsonContent<T>(T value)
        => new(JsonSerializer.Serialize(value, TigerCityJsonOptions), Encoding.UTF8, "application/json");

    private TigerCityGenerateRequest CreateTigerCityRequest(AIRequest request, bool stream)
    {
        var messages = BuildMessages(request);

        if (messages.Count == 0)
            messages.Add(new TigerCityMessage { Role = "user", Content = string.Empty });

        return new TigerCityGenerateRequest
        {
            Model = request.Model ?? string.Empty,
            Messages = messages,
            Stream = stream,
            Temperature = request.Temperature,
            TopP = request.TopP
        };
    }

    private static List<TigerCityMessage> BuildMessages(AIRequest request)
    {
        var messages = new List<TigerCityMessage>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            AddMessage(messages, "system", request.Instructions!);

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            AddMessage(messages, "user", request.Input.Text!);

        if (request.Input?.Items is null)
            return messages;

        foreach (var item in request.Input.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Type)
                && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var role = NormalizeRole(item.Role);
            if (role is null)
                continue;

            var content = ExtractText(item.Content);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            AddMessage(messages, role, content);
        }

        return messages;
    }

    private static void AddMessage(List<TigerCityMessage> messages, string role, string content)
    {
        if (role == "system" && messages.Any(message => message.Role == "system"))
        {
            var systemMessage = messages.First(message => message.Role == "system");
            messages[messages.IndexOf(systemMessage)] = new TigerCityMessage
            {
                Role = "system",
                Content = string.Join("\n\n", [systemMessage.Content, content.Trim()])
            };
            return;
        }

        messages.Add(new TigerCityMessage
        {
            Role = role,
            Content = content.Trim()
        });
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "user";

        var normalized = role.Trim().ToLowerInvariant();
        return normalized is "system" or "user" or "assistant" ? normalized : null;
    }

    private static string ExtractText(List<AIContentPart>? content)
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
                case AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                    textParts.Add(reasoningPart.Text!.Trim());
                    break;
            }
        }

        return string.Join("\n\n", textParts);
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, TigerCityGenerateResponse payload)
    {
        var text = payload.Message?.Content ?? string.Empty;
        var metadata = CreateBaseMetadata(request, payload.Id, payload.Usage);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = CreateOutput(text),
            Usage = CreateUsageDictionary(payload.Usage),
            Metadata = metadata
        };
    }

    private static AIOutput CreateOutput(string text)
        => new()
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
                            Text = text
                        }
                    ]
                }
            ]
        };

    private Dictionary<string, object?> CreateBaseMetadata(AIRequest request, string? responseId, TigerCityUsage? usage)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["tigercity.provider"] = GetIdentifier(),
            ["tigercity.model"] = request.Model,
            ["tigercity.response_id"] = responseId,
            ["tigercity.duration_ms"] = usage?.DurationMs
        };

        if (request.Metadata is not null)
        {
            foreach (var item in request.Metadata)
                metadata[item.Key] = item.Value;
        }

        return metadata;
    }

    private static Dictionary<string, object?> CreateUsageDictionary(TigerCityUsage? usage)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["input_tokens"] = usage?.InputTokens,
            ["output_tokens"] = usage?.OutputTokens,
            ["total_tokens"] = CalculateTotalTokens(usage),
            ["duration_ms"] = usage?.DurationMs
        };

    private static int? CalculateTotalTokens(TigerCityUsage? usage)
    {
        if (usage?.InputTokens is null && usage?.OutputTokens is null)
            return null;

        return (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);
    }

    private static AIFinishMessageMetadata CreateFinishMetadata(
        AIRequest request,
        TigerCityUsage? usage,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => AIFinishMessageMetadata.Create(
            model: request.Model ?? string.Empty,
            timestamp: timestamp,
            usage: CreateUsageDictionary(usage),
            outputTokens: usage?.OutputTokens,
            inputTokens: usage?.InputTokens,
            totalTokens: CalculateTotalTokens(usage),
            temperature: request.Temperature,
            runtimeMs: usage?.DurationMs is null ? null : (long)usage.DurationMs.Value,
            additionalProperties: metadata);

    private static IEnumerable<AIStreamEvent> CreateInfoToolEvents(
        string providerId,
        string eventId,
        string? message,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
    {
        var toolCallId = $"{eventId}:tigercity-info:{Guid.NewGuid():N}";
        var input = JsonSerializer.SerializeToElement(new { message }, TigerCityJsonOptions);
        var output = JsonSerializer.SerializeToElement(new { }, TigerCityJsonOptions);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-input-start",
            new AIToolInputStartEventData
            {
                ToolName = TigerCityInfoToolName,
                Title = TigerCityInfoToolTitle,
                ProviderExecuted = true
            },
            timestamp,
            metadata);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-input-available",
            new AIToolInputAvailableEventData
            {
                ToolName = TigerCityInfoToolName,
                Title = TigerCityInfoToolTitle,
                Input = input,
                ProviderExecuted = true
            },
            timestamp,
            metadata);

        yield return CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-output-available",
            new AIToolOutputAvailableEventData
            {
                ToolName = TigerCityInfoToolName,
                Output = output,
                ProviderExecuted = true
            },
            timestamp,
            metadata);
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

    private static Exception CreateTigerCityException(HttpResponseMessage response, string raw)
        => new HttpRequestException($"TigerCity API error: {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractErrorMessage(raw)}");

    private static string ExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown TigerCity error.";

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("error", out var error))
                    return error.ValueKind == JsonValueKind.String ? error.GetString() ?? raw : error.GetRawText();

                if (document.RootElement.TryGetProperty("message", out var message))
                    return message.ValueKind == JsonValueKind.String ? message.GetString() ?? raw : message.GetRawText();
            }
        }
        catch
        {
            // Fall through to the raw response body.
        }

        return raw;
    }

    private sealed class TigerCityGenerateRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required List<TigerCityMessage> Messages { get; init; }

        [JsonPropertyName("stream")]
        public required bool Stream { get; init; }

        [JsonPropertyName("temperature")]
        public float? Temperature { get; init; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; init; }
    }

    private sealed class TigerCityMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private sealed class TigerCityGenerateResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("message")]
        public TigerCityMessage? Message { get; init; }

        [JsonPropertyName("usage")]
        public TigerCityUsage? Usage { get; init; }
    }

    private sealed class TigerCityUsage
    {
        [JsonPropertyName("duration_ms")]
        public double? DurationMs { get; init; }

        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; init; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; init; }
    }

    private sealed class TigerCityStreamChunk
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("delta")]
        public string? Delta { get; init; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; init; }

        [JsonPropertyName("usage")]
        public TigerCityUsage? Usage { get; init; }
    }
}
