using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private async Task<AIResponse> ExecuteParallelChatCompletionUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var response = await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        return EnrichParallelChatCompletionUnifiedResponse(response);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamParallelChatCompletionUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new ParallelBasisStreamState();

        await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken))
        {
            foreach (var basisEvent in CreateParallelBasisStreamEvents(streamEvent, state))
                yield return basisEvent;

            yield return streamEvent;
        }
    }

    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        NormalizeParallelChatCompletionOptions(options);

        return await this.GetChatCompletion(
            _client,
            options,
            relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        NormalizeParallelChatCompletionOptions(options);

        options.Stream = true;
        var textStarted = false;

        await foreach (var update in this.GetChatCompletions(
                           _client,
                           options,
                           relativeUrl: ChatCompletionsPath,
                           cancellationToken: cancellationToken))
        {
            var normalized = NormalizeParallelStreamChunkId(update);
            if (!textStarted && HasTextDelta(normalized))
            {
                textStarted = true;
                yield return CreateParallelTextStartChunk(normalized);
            }

            if (HasFinishReason(normalized))
                textStarted = false;

            yield return normalized;
        }
    }

    private static ChatCompletionUpdate NormalizeParallelStreamChunkId(ChatCompletionUpdate update)
    {
        var interactionId = update.AdditionalProperties is not null
            && update.AdditionalProperties.TryGetValue("interaction_id", out var interactionEl)
            && interactionEl.ValueKind == JsonValueKind.String
                ? interactionEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(interactionId))
            return update;

        update.Id = interactionId!;
        return update;
    }

    private static bool HasTextDelta(ChatCompletionUpdate update)
        => update.Choices.Any(ChoiceHasTextDelta);

    private static bool ChoiceHasTextDelta(object choice)
    {
        var root = JsonSerializer.SerializeToElement(choice, Json);
        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return false;

        return delta.TryGetProperty("content", out var content)
               && content.ValueKind == JsonValueKind.String
               && !string.IsNullOrEmpty(content.GetString());
    }

    private static bool HasFinishReason(ChatCompletionUpdate update)
        => update.Choices.Any(choice =>
        {
            var root = JsonSerializer.SerializeToElement(choice, Json);
            return root.TryGetProperty("finish_reason", out var finish)
                   && finish.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(finish.GetString());
        });

    private static ChatCompletionUpdate CreateParallelTextStartChunk(ChatCompletionUpdate update)
        => new()
        {
            Id = update.Id,
            Object = update.Object,
            Created = update.Created,
            Model = update.Model,
            ServiceTier = update.ServiceTier,
            Choices = update.Choices.Select(CreateParallelTextStartChoice).ToList(),
            Usage = null,
            AdditionalProperties = update.AdditionalProperties
        };

    private static object CreateParallelTextStartChoice(object choice)
    {
        var root = JsonSerializer.SerializeToElement(choice, Json);
        var index = root.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
            ? indexEl.GetInt32()
            : 0;

        return new
        {
            index,
            delta = new { role = "assistant" },
            finish_reason = (string?)null
        };
    }

    private static void NormalizeParallelChatCompletionOptions(ChatCompletionOptions options)
    {
        options.Model = NormalizeParallelChatCompletionModel(options.Model);
        options.Messages = options.Messages.Select(message => new ChatMessage
        {
            Role = NormalizeRole(message.Role),
            Content = JsonSerializer.SerializeToElement(FlattenCompletionMessageContent(message.Content), Json)
        }).ToList();
    }

    private static string NormalizeParallelChatCompletionModel(string model)
        => string.IsNullOrWhiteSpace(model)
            ? model
            : model.StartsWith("parallel/", StringComparison.OrdinalIgnoreCase)
                ? model["parallel/".Length..]
                : model;

    private AIResponse EnrichParallelChatCompletionUnifiedResponse(AIResponse response)
    {
        var metadata = response.Metadata;
        var raw = ExtractMetadataJson(metadata, "chatcompletions.response.raw");
        if (raw is null || !TryGetProperty(raw.Value, "basis", out var basis) || basis.ValueKind != JsonValueKind.Array)
            return response;

        var outputItems = response.Output?.Items?.ToList() ?? [];
        outputItems.AddRange(CreateParallelBasisReasoningOutputItems(basis));
        outputItems.AddRange(CreateParallelBasisSourceOutputItems(basis));

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Usage = response.Usage,
            Metadata = response.Metadata,
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = response.Output?.Metadata
            }
        };
    }

    private IEnumerable<AIStreamEvent> CreateParallelBasisStreamEvents(
        AIStreamEvent streamEvent,
        ParallelBasisStreamState state)
    {
        var raw = ExtractMetadataJson(streamEvent.Metadata, "chatcompletions.stream.raw");
        if (raw is null || !TryGetProperty(raw.Value, "basis", out var basis) || basis.ValueKind != JsonValueKind.Array)
            yield break;

        var timestamp = streamEvent.Event.Timestamp ?? DateTimeOffset.UtcNow;
        var baseId = TryGetString(raw.Value, "interaction_id") ?? streamEvent.Event.Id ?? TryGetString(raw.Value, "id") ?? Guid.NewGuid().ToString("N");

        foreach (var fieldBasis in basis.EnumerateArray())
        {
            var field = TryGetString(fieldBasis, "field") ?? "answer";
            var reasoning = TryGetString(fieldBasis, "reasoning");
            if (!string.IsNullOrWhiteSpace(reasoning) && state.SeenReasoningFields.Add(field))
            {
                var reasoningId = $"{baseId}:basis:{field}:reasoning";
                var providerMetadata = ToProviderMetadata(GetIdentifier(), new Dictionary<string, object?>
                {
                    ["basis"] = fieldBasis.Clone(),
                    ["field"] = field,
                    ["confidence"] = TryGetString(fieldBasis, "confidence")
                });

                yield return CreateParallelStreamEvent(
                    GetIdentifier(),
                    "reasoning-start",
                    reasoningId,
                    new AIReasoningStartEventData { ProviderMetadata = providerMetadata },
                    timestamp);

                yield return CreateParallelStreamEvent(
                    GetIdentifier(),
                    "reasoning-delta",
                    reasoningId,
                    new AIReasoningDeltaEventData
                    {
                        Delta = reasoning!,
                        ProviderMetadata = providerMetadata
                    },
                    timestamp);

                yield return CreateParallelStreamEvent(
                    GetIdentifier(),
                    "reasoning-end",
                    reasoningId,
                    new AIReasoningEndEventData { ProviderMetadata = providerMetadata },
                    timestamp);
            }

            foreach (var citation in EnumerateParallelBasisCitations(fieldBasis))
            {
                var url = TryGetString(citation, "url");
                if (string.IsNullOrWhiteSpace(url) || !state.SeenSourceUrls.Add(url))
                    continue;

                yield return CreateParallelStreamEvent(
                    GetIdentifier(),
                    "source-url",
                    $"{baseId}:source:{state.SeenSourceUrls.Count}",
                    new AISourceUrlEventData
                    {
                        SourceId = url!,
                        Url = url!,
                        Title = TryGetString(citation, "title") ?? url,
                        Type = "parallel_basis_citation",
                        ProviderMetadata = ToProviderMetadata(GetIdentifier(), new Dictionary<string, object?>
                        {
                            ["citation"] = citation.Clone(),
                            ["field"] = field,
                            ["excerpts"] = TryGetProperty(citation, "excerpts", out var excerpts) ? excerpts.Clone() : null
                        })
                    },
                    timestamp);
            }
        }
    }

    private IEnumerable<AIOutputItem> CreateParallelBasisReasoningOutputItems(JsonElement basis)
    {
        foreach (var fieldBasis in basis.EnumerateArray())
        {
            var reasoning = TryGetString(fieldBasis, "reasoning");
            if (string.IsNullOrWhiteSpace(reasoning))
                continue;

            yield return new AIOutputItem
            {
                Type = "reasoning",
                Role = "assistant",
                Content =
                [
                    new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = reasoning,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["parallel.basis"] = fieldBasis.Clone(),
                            ["parallel.field"] = TryGetString(fieldBasis, "field"),
                            ["parallel.confidence"] = TryGetString(fieldBasis, "confidence")
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["parallel.basis"] = fieldBasis.Clone(),
                    ["parallel.field"] = TryGetString(fieldBasis, "field"),
                    ["parallel.confidence"] = TryGetString(fieldBasis, "confidence")
                }
            };
        }
    }

    private IEnumerable<AIOutputItem> CreateParallelBasisSourceOutputItems(JsonElement basis)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldBasis in basis.EnumerateArray())
        {
            var field = TryGetString(fieldBasis, "field") ?? "answer";
            foreach (var citation in EnumerateParallelBasisCitations(fieldBasis))
            {
                var url = TryGetString(citation, "url");
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                    continue;

                yield return new AIOutputItem
                {
                    Type = "source-url",
                    Content = [],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["title"] = TryGetString(citation, "title") ?? url,
                        ["parallel.citation"] = citation.Clone(),
                        ["parallel.field"] = field,
                        ["parallel.excerpts"] = TryGetProperty(citation, "excerpts", out var excerpts) ? excerpts.Clone() : null
                    }
                };
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateParallelBasisCitations(JsonElement fieldBasis)
    {
        if (!TryGetProperty(fieldBasis, "citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var citation in citations.EnumerateArray())
        {
            if (citation.ValueKind == JsonValueKind.Object)
                yield return citation;
        }
    }

    private static JsonElement? ExtractMetadataJson(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        return value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, Json);
    }

    private sealed class ParallelBasisStreamState
    {
        public HashSet<string> SeenReasoningFields { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SeenSourceUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
