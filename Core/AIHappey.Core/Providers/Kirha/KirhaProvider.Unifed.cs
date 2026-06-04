using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        if (IsKirhaTasksModel(request.Model))
            return await ExecuteKirhaTaskUnifiedAsync(request, cancellationToken);

        var context = CreateKirhaRequestContext(request);
        var result = await ExecuteKirhaSearchAsync(context, cancellationToken);

        return CreateUnifiedResponse(request, result);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsKirhaTasksModel(request.Model))
        {
            await foreach (var update in StreamKirhaTaskUnifiedAsync(request, cancellationToken))
                yield return update;

            yield break;
        }

        var (response, error) = await TryExecuteUnifiedForStreamAsync(request, cancellationToken);
        if (error is not null)
        {
            yield return CreateStreamEvent(
                request.ProviderId,
                request.Id ?? Guid.NewGuid().ToString("N"),
                "error",
                new AIErrorEventData { ErrorText = error.Message },
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["kirha.error.type"] = error.GetType().FullName
                });
            yield break;
        }

        if (response is null)
            yield break;

        var providerId = GetIdentifier();
        var eventId = request.Id ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;

        foreach (var item in response.Output?.Items ?? [])
        {
            foreach (var content in item.Content ?? [])
            {
                switch (content)
                {
                    case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        {
                            var id = content.Metadata?.GetProviderOption<string>(providerId, "id")
                                     ?? $"{eventId}:reasoning:{Guid.NewGuid():N}";
                            var providerMetadata = ToProviderMetadataEnvelope(content.Metadata, providerId);

                            yield return CreateStreamEvent(providerId, id, "reasoning-start",
                                new AIReasoningStartEventData { ProviderMetadata = providerMetadata }, timestamp, content.Metadata);
                            yield return CreateStreamEvent(providerId, id, "reasoning-delta",
                                new AIReasoningDeltaEventData { Delta = reasoning.Text!, ProviderMetadata = providerMetadata }, timestamp, content.Metadata);
                            yield return CreateStreamEvent(providerId, id, "reasoning-end",
                                new AIReasoningEndEventData { ProviderMetadata = providerMetadata }, timestamp, content.Metadata);
                            break;
                        }

                    case AIToolCallContentPart tool:
                        {
                            var providerMetadata = ToProviderMetadataEnvelope(tool.Metadata, providerId);
                            yield return CreateStreamEvent(providerId, tool.ToolCallId, "tool-input-available",
                                new AIToolInputAvailableEventData
                                {
                                    ToolName = tool.ToolName ?? tool.Title ?? "kirha_tool",
                                    Title = tool.Title,
                                    Input = tool.Input ?? new { },
                                    ProviderExecuted = tool.ProviderExecuted,
                                    ProviderMetadata = providerMetadata
                                }, timestamp, tool.Metadata);

                            yield return CreateStreamEvent(providerId, tool.ToolCallId, "tool-output-available",
                                new AIToolOutputAvailableEventData
                                {
                                    ToolName = tool.ToolName,
                                    Output = tool.Output ?? new { },
                                    ProviderExecuted = tool.ProviderExecuted,
                                    ProviderMetadata = providerMetadata
                                }, timestamp, tool.Metadata);
                            break;
                        }

                    case AITextContentPart text when !string.IsNullOrWhiteSpace(text.Text):
                        {
                            var id = $"{eventId}:text";
                            yield return CreateStreamEvent(providerId, id, "text-start", new AITextStartEventData(), timestamp, text.Metadata);
                            yield return CreateStreamEvent(providerId, id, "text-delta",
                                new AITextDeltaEventData { Delta = text.Text }, timestamp, text.Metadata);
                            yield return CreateStreamEvent(providerId, id, "text-end", new AITextEndEventData(), timestamp, text.Metadata);
                            break;
                        }
                }
            }
        }

        var usage = ExtractUsage(response.Usage);
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
                    FinishReason = response.Status == "failed" ? "error" : "stop",
                    Model = response.Model?.ToModelId(GetIdentifier()),
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = usage.InputTokens,
                    OutputTokens = usage.OutputTokens,
                    TotalTokens = usage.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? request.Model ?? string.Empty,
                        timestamp,
                        response.Usage,
                        outputTokens: usage.OutputTokens,
                        inputTokens: usage.InputTokens,
                        totalTokens: usage.TotalTokens,
                        temperature: request.Temperature,
                        additionalProperties: response.Metadata)
                }
            },
            Metadata = response.Metadata
        };
    }
}
