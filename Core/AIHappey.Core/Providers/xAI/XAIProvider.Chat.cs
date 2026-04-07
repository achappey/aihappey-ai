using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model.Type)
        {
            case "image":
                await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "speech":
                await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "video":
                await foreach (var p in this.StreamVideoAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
        }

        ApplyAuthHeader();

        await foreach (var part in this.StreamResponsesAsync(
            chatRequest,
            CreateResponsesStreamRequestAsync,
            CreateResponsesStreamMappingOptions,
            PostProcessResponsesStreamPartAsync,
            cancellationToken))
        {
            yield return part;
        }
    }

    private ValueTask<ResponseRequest> CreateResponsesStreamRequestAsync(
        ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        var metadata = chatRequest.GetProviderMetadata<XAIProviderMetadata>(GetIdentifier());
        var request = chatRequest.ToResponsesRequest(GetIdentifier(), new ResponsesRequestMappingOptions
        {
            Instructions = metadata?.Instructions,
            Include = metadata?.Include,
            Metadata = chatRequest.ProviderMetadata.ToObjectDictionary(),
            Reasoning = metadata?.Reasoning != null ? new Reasoning()
            {
                //    Effort = metadata?.Reasoning.Effort,
                Summary = metadata?.Reasoning.Summary,
            } : null,
            Store = false,
            ParallelToolCalls = metadata?.ParallelToolCalls,
            Tools = chatRequest.Tools?.Select(a => a.ToResponseToolDefinition()),
            ToolChoice = chatRequest.ToolChoice
        });

        return ValueTask.FromResult(request);
    }

    private async IAsyncEnumerable<UIMessagePart> MapXAIOutputItemDoneAsync(
        ResponseOutputItemDone outputItemDone,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.Equals(outputItemDone.Item.Type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            var metadata =
                JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(outputItemDone.Item.AdditionalProperties)
                ) ?? [];

            metadata["id"] = outputItemDone.Item.Id!;

            yield return new ReasoningStartUIPart
            {
                Id = outputItemDone.Item.Id!
            };

            var summaries =
                outputItemDone.Item.AdditionalProperties?
                    .TryGetValue("summary", out var summaryEl) == true &&
                summaryEl.ValueKind == JsonValueKind.Array
                    ? summaryEl.Deserialize<List<ResponseReasoningSummaryTextPart>>() ?? []
                    : [];

            yield return new ReasoningDeltaUIPart
            {
                Id = outputItemDone.Item.Id!,
                Delta = string.Join("\n\n", summaries?.Select(a => a.Text) ?? [outputItemDone.Item.Id])
            };


            yield return new ReasoningEndUIPart
            {
                Id = outputItemDone.Item.Id!,
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    [GetIdentifier()] = metadata
                },
            };
        }


        await Task.CompletedTask;
    }


    private ResponsesStreamMappingOptions CreateResponsesStreamMappingOptions(ChatRequest chatRequest)
    {
        var metadata = chatRequest.GetProviderMetadata<XAIProviderMetadata>(GetIdentifier());

        return new ResponsesStreamMappingOptions
        {
            ProviderExecutedTools = metadata?.Tools?
                .Select(x => x.GetProperty("type").GetString())
                .OfType<string>()
                .ToArray(),
            OutputItemDoneMapper = (outputItemDone, context, ct) => MapXAIOutputItemDoneAsync(outputItemDone, ct),
            ResolveToolTitle = toolName => chatRequest.Tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
            FinishFactory = response =>
            {
                var extraMetadata = CreateGatewayCostMetadata(response.Usage);

                return "stop".ToFinishUIPart(
                    response.Model.ToModelId(GetIdentifier()),
                    GetUsageValue(response.Usage, "output_tokens", "outputTokens") ?? 0,
                    GetUsageValue(response.Usage, "input_tokens", "inputTokens") ?? 0,
                    ModelCostMetadataEnricher.GetTotalTokens(response.Usage) ?? 0,
                    response.Temperature,
                    reasoningTokens: GetUsageValue(response.Usage, "reasoning_tokens", "reasoningTokens"),
                    extraMetadata: extraMetadata);
            }
        };
    }

    private async IAsyncEnumerable<UIMessagePart> PostProcessResponsesStreamPartAsync(
        UIMessagePart part,
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return part;
        await Task.CompletedTask;
    }

    private static int? GetUsageValue(object? usage, params string[] propertyNames)
    {
        if (usage == null)
            return null;

        try
        {
            var json = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            foreach (var name in propertyNames)
            {
                if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    return number;
            }

            if (json.TryGetProperty("output_tokens_details", out var outputDetails))
            {
                foreach (var name in propertyNames)
                {
                    if (outputDetails.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                        return number;
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
