using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AddisAI;

public partial class AddisAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeModelId(request.Model);

        return model.StartsWith(TranslationPrefix, StringComparison.OrdinalIgnoreCase)
            ? await ExecuteTranslationUnifiedAsync(request, cancellationToken)
            : await ExecuteChatUnifiedAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;
        var eventId = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateTextStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreateTextStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateTextStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);

        var usage = response.Usage as Dictionary<string, object?>;
        yield return CreateTextStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = response.Metadata?.TryGetValue("finishReason", out var finishReason) == true
                    ? finishReason?.ToString() ?? "stop"
                    : "stop",
                Model = response.Model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? $"{GetIdentifier()}/{ChatModel}",
                    DateTimeOffset.UtcNow,
                    usage,
                    outputTokens: TryGetUsageValue(usage, "outputTokens"),
                    inputTokens: TryGetUsageValue(usage, "inputTokens"),
                    totalTokens: TryGetUsageValue(usage, "totalTokens"),
                    temperature: request.Temperature)
            },
            DateTimeOffset.UtcNow,
            response.Metadata);
    }

    private async Task<AIResponse> ExecuteChatUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = NormalizeModelId(request.Model);
        var targetLanguage = GetChatTargetLanguage(model);
        var messages = ExtractMessages(request);
        var lastUserIndex = messages.FindLastIndex(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUserIndex < 0)
            throw new ArgumentException("AddisAI chat requires at least one user text message.", nameof(request));

        var prompt = messages[lastUserIndex].Content;
        var history = messages.Take(lastUserIndex)
            .Select(message => new { role = NormalizeChatRole(message.Role), content = message.Content })
            .ToArray();
        var generationConfig = new Dictionary<string, object?>
        {
            ["stream"] = false,
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = request.MaxOutputTokens,
            ["topP"] = request.TopP
        };
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["target_language"] = targetLanguage,
            ["conversation_history"] = history,
            ["generation_config"] = generationConfig
        };

        ApplyAuthHeader();
        var payloadJson = JsonSerializer.Serialize(payload, AddisJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat_generate")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AddisAI chat generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var text = GetRequiredString(root, "response_text", raw);
        var usage = BuildUsage(root);
        var providerModel = root.TryGetProperty("modelVersion", out var modelVersion) && modelVersion.ValueKind == JsonValueKind.String
            ? modelVersion.GetString()
            : ChatModel;

        return CreateTextResponse(
            request.Model ?? $"{GetIdentifier()}/{ChatModel}",
            text,
            usage,
            new()
            {
                ["finishReason"] = root.TryGetProperty("finish_reason", out var finishReason) ? finishReason.GetString() ?? "stop" : "stop",
                ["modelVersion"] = providerModel,
                ["addisai.response.raw"] = root.Clone()
            });
    }

    private AIResponse CreateTextResponse(
        string model,
        string text,
        Dictionary<string, object?> usage,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Usage = usage,
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = text }]
                    }
                ]
            }
        };

    private AIStreamEvent CreateTextStreamEvent(
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };

    private static string NormalizeChatRole(string role)
        => string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";

    private static int? TryGetUsageValue(Dictionary<string, object?>? usage, string name)
        => usage?.TryGetValue(name, out var value) == true && value is int number ? number : null;
}
