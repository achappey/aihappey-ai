using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null)
            throw new NotSupportedException("Synexa generic language predictions do not support tool calls.");

        var (prompt, systemPrompt) = BuildSynexaUnifiedPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Synexa language predictions require text input.", nameof(request));

        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = prompt,
            ["system_prompt"] = systemPrompt,
            ["temperature"] = request.Temperature,
            ["top_p"] = request.TopP,
            ["max_tokens"] = request.MaxOutputTokens,
            ["max_new_tokens"] = request.MaxOutputTokens
        };

        var providerMetadata = GetSynexaUnifiedProviderMetadata(request.Metadata);
        MergeSynexaInputMetadata(input, providerMetadata, "prompt", "system_prompt", "temperature", "top_p", "max_tokens", "max_new_tokens");

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, GetSynexaWaitOptions(providerMetadata), cancellationToken);
        var text = ExtractOutputText(completed.Output);
        var metadata = CreateSynexaPredictionMetadata(completed);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = text }],
                        Metadata = metadata
                    }
                ],
                Metadata = metadata
            },
            Usage = completed.Metrics.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : completed.Metrics.Clone(),
            Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;

        yield return CreateSynexaUnifiedEvent(id, "text-start", new AITextStartEventData(), timestamp, response.Metadata);

        if (!string.IsNullOrEmpty(text))
            yield return CreateSynexaUnifiedEvent(id, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);

        yield return CreateSynexaUnifiedEvent(id, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = id,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    Response = response
                }
            }
        };
    }

    private AIStreamEvent CreateSynexaUnifiedEvent(
        string id,
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
                Id = id,
                Timestamp = timestamp,
                Data = data
            }
        };

    private JsonElement GetSynexaUnifiedProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return default;

        if (metadata.TryGetValue(GetIdentifier(), out var nested) && nested is not null)
            return JsonSerializer.SerializeToElement(nested, SynexaJson);

        return JsonSerializer.SerializeToElement(metadata, SynexaJson);
    }

    private static (string Prompt, string? SystemPrompt) BuildSynexaUnifiedPrompt(AIRequest request)
    {
        var system = new List<string>();
        var conversation = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            system.Add(request.Instructions.Trim());
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            conversation.Add(request.Input.Text.Trim());

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = string.Join("\n", (item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var role = item.Role?.Trim().ToLowerInvariant() ?? "user";
            if (role == "system")
                system.Add(text);
            else if (role is "user" or "assistant")
                conversation.Add($"{role}: {text}");
        }

        return (string.Join("\n\n", conversation), system.Count == 0 ? null : string.Join("\n\n", system));
    }
}
