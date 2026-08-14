using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.GooseAI;

public partial class GooseAIProvider
{
    private static readonly JsonSerializerOptions GooseJson = JsonSerializerOptions.Web;

    private static readonly HashSet<string> GooseCompletionOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "min_tokens", "logit_bias", "stop", "top_k", "tfs", "top_a", "typical_p",
        "logprobs", "echo", "presence_penalty", "frequency_penalty", "repetition_penalty",
        "repetition_penalty_slope", "repetition_penalty_range"
    };

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGooseRequest(request);

        var engine = NormalizeGooseEngine(request.Model);
        var payload = BuildGoosePayload(request, stream: false);
        ApplyAuthHeader();

        using var message = CreateGooseRequest(engine, payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureGooseSuccess(response, raw);

        using var document = JsonDocument.Parse(raw);
        ThrowGoosePayloadError(document.RootElement);
        return MapGooseResponse(request, engine, document.RootElement);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGooseRequest(request);

        var engine = NormalizeGooseEngine(request.Model);
        var payload = BuildGoosePayload(request, stream: true);
        ApplyAuthHeader();

        using var message = CreateGooseRequest(engine, payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureGooseSuccess(response, rawError);
        }

        var responseId = request.Id ?? Guid.NewGuid().ToString("N");
        var startedChoices = new HashSet<int>();
        var completedChoices = new HashSet<int>();
        var texts = new Dictionary<int, StringBuilder>();
        var finishReasons = new Dictionary<int, string?>();
        AIUsage? usage = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            ThrowGoosePayloadError(root);
            usage = ReadGooseUsage(root) ?? usage;

            foreach (var choice in ReadGooseChoices(root))
            {
                var choiceId = ChoiceEventId(responseId, choice.Index);
                if (startedChoices.Add(choice.Index))
                    yield return CreateGooseEvent(choiceId, "text-start", new AITextStartEventData());

                if (!texts.TryGetValue(choice.Index, out var text))
                    texts[choice.Index] = text = new StringBuilder();

                if (!string.IsNullOrEmpty(choice.Text))
                {
                    text.Append(choice.Text);
                    yield return CreateGooseEvent(choiceId, "text-delta", new AITextDeltaEventData { Delta = choice.Text });
                }

                if (choice.FinishReason is not null)
                {
                    finishReasons[choice.Index] = NormalizeGooseFinishReason(choice.FinishReason);
                    if (completedChoices.Add(choice.Index))
                        yield return CreateGooseEvent(choiceId, "text-end", new AITextEndEventData());
                }
            }
        }

        foreach (var index in startedChoices.Order())
        {
            if (completedChoices.Add(index))
                yield return CreateGooseEvent(ChoiceEventId(responseId, index), "text-end", new AITextEndEventData());
        }

        var output = CreateGooseOutput(texts.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToString()));
        var now = DateTimeOffset.UtcNow;
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Id = responseId,
                Type = "finish",
                Timestamp = now,
                Output = output,
                Data = new AIFinishEventData
                {
                    FinishReason = finishReasons.OrderBy(pair => pair.Key).Select(pair => pair.Value).FirstOrDefault() ?? "stop",
                    Model = engine.ToModelId(GetIdentifier()),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    InputTokens = usage?.InputTokens,
                    OutputTokens = usage?.OutputTokens,
                    TotalTokens = usage?.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        engine.ToModelId(GetIdentifier()), now, usage,
                        usage?.OutputTokens, usage?.InputTokens, usage?.TotalTokens,
                        request.Temperature)
                }
            },
            Metadata = CreateGooseMetadata(engine)
        };
    }

    private Dictionary<string, object?> BuildGoosePayload(AIRequest request, bool stream)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (GooseCompletionOptions.Contains(property.Name))
                    payload[property.Name] = property.Value.Clone();
            }
        }

        payload["prompt"] = GetLatestGooseUserText(request);
        payload["stream"] = stream;
        if (request.Temperature is not null) payload["temperature"] = request.Temperature;
        if (request.TopP is not null) payload["top_p"] = request.TopP;
        if (request.MaxOutputTokens is not null) payload["max_tokens"] = request.MaxOutputTokens;
        return payload;
    }

    private static string GetLatestGooseUserText(AIRequest request)
    {
        if (request.Input?.Items is { Count: > 0 })
        {
            for (var index = request.Input.Items.Count - 1; index >= 0; index--)
            {
                var item = request.Input.Items[index];
                if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                var textParts = (item.Content ?? []).OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                if ((item.Content ?? []).Any(part => part is not AITextContentPart))
                    throw new NotSupportedException("GooseAI completion input supports text content only.");
                if (textParts.Count > 0)
                    return string.Join("\n", textParts);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;

        throw new ArgumentException("GooseAI requires a non-empty user text message.", nameof(request));
    }

    private static void ValidateGooseRequest(AIRequest request)
    {
        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null)
            throw new NotSupportedException("GooseAI native completions do not support tools or tool choice.");
        if (request.ResponseFormat is not null)
            throw new NotSupportedException("GooseAI native completions do not support structured response formats.");
    }

    private static string NormalizeGooseEngine(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A GooseAI engine ID is required.", nameof(model));
        const string prefix = "gooseai/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static HttpRequestMessage CreateGooseRequest(string engine, Dictionary<string, object?> payload)
        => new(HttpMethod.Post, $"v1/engines/{Uri.EscapeDataString(engine)}/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, GooseJson), Encoding.UTF8, "application/json")
        };

    private AIResponse MapGooseResponse(AIRequest request, string engine, JsonElement root)
    {
        var choices = ReadGooseChoices(root).OrderBy(choice => choice.Index).ToList();
        var usage = ReadGooseUsage(root);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = engine.ToModelId(GetIdentifier()),
            Status = "completed",
            Output = CreateGooseOutput(choices.Select(choice => choice.Text)),
            Usage = usage,
            Metadata = new Dictionary<string, object?>(CreateGooseMetadata(engine))
            {
                ["gooseai.id"] = ReadString(root, "id"),
                ["gooseai.finish_reasons"] = choices.Select(choice => NormalizeGooseFinishReason(choice.FinishReason)).ToArray()
            }
        };
    }

    private static AIOutput CreateGooseOutput(IEnumerable<string> texts)
        => new()
        {
            Items = texts.Select(text => new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = text }]
            }).ToList()
        };

    private AIStreamEvent CreateGooseEvent(string id, string type, object data)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data }
        };

    private Dictionary<string, object?> CreateGooseMetadata(string engine)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["gooseai.engine"] = engine,
            ["gooseai.history_ignored"] = true
        };

    private static IEnumerable<GooseChoice> ReadGooseChoices(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        var fallbackIndex = 0;
        foreach (var choice in choices.EnumerateArray())
        {
            var index = choice.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : fallbackIndex;
            yield return new GooseChoice(index, ReadString(choice, "text") ?? string.Empty, ReadString(choice, "finish_reason"));
            fallbackIndex++;
        }
    }

    private static AIUsage? ReadGooseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var input = ReadInt(usage, "prompt_tokens");
        var output = ReadInt(usage, "completion_tokens");
        var total = ReadInt(usage, "total_tokens") ?? (input is null && output is null ? null : (input ?? 0) + (output ?? 0));
        return new AIUsage { InputTokens = input, OutputTokens = output, TotalTokens = total };
    }

    private static string NormalizeGooseFinishReason(string? reason)
        => reason?.ToLowerInvariant() switch
        {
            "length" => "length",
            "content_filter" => "content-filter",
            null or "" or "stop" => "stop",
            _ => reason
        };

    private static void EnsureGooseSuccess(HttpResponseMessage response, string raw)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GooseAI completion request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {ExtractGooseError(raw)}");
    }

    private static void ThrowGoosePayloadError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"GooseAI completion error: {ExtractGooseError(error.GetRawText())}");
    }

    private static string ExtractGooseError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown GooseAI error.";
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? raw;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? raw;
                if (error.ValueKind == JsonValueKind.Object && ReadString(error, "message") is { } nested) return nested;
            }
            if (ReadString(root, "message") is { } message) return message;
        }
        catch (JsonException)
        {
        }
        return raw;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string ChoiceEventId(string responseId, int index) => index == 0 ? responseId : $"{responseId}:{index}";

    private sealed record GooseChoice(int Index, string Text, string? FinishReason);
}
