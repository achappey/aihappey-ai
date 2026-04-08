using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!chatRequest.Model.StartsWith($"sonar"))
        {
            await foreach (var part in this.StreamResponsesAsync(
                chatRequest,
                requestFactory: (request, ct) => ValueTask.FromResult(CreateResponsesRequest(request)),
                mappingOptionsFactory: _ => new ResponsesStreamMappingOptions()
                {
                    BeforeFinishMapper = (outputItemDone, ct) => MapPerplexityBeforeFinishAsync(outputItemDone, GetIdentifier(), ct),
                    OutputItemDoneMapper = (outputItemDone, context, ct) => MapPerplexityOutputItemDoneAsync(outputItemDone, GetIdentifier(), ct),
                    FinishFactory = response => CreatePerplexityFinishPart(response)
                },
                cancellationToken: cancellationToken))
            {
                yield return part;
            }

            yield break;
        }

        var (messages, systemRole) = chatRequest.Messages.ToPerplexityMessages();
        var req = chatRequest.ToChatRequest(messages, systemRole);

        string? currentStreamId = null;

        HashSet<string> sources = [];

        await foreach (var chunk in _client.ChatCompletionStreaming(req, cancellationToken))
        {
            var firstChoice = chunk?.Choices?.FirstOrDefault();
            var content = firstChoice?.Delta?.Content
                ?? firstChoice?.Message?.Content;

            if (!string.IsNullOrEmpty(content))
            {
                if (currentStreamId is null && chunk?.Id is not null)
                {
                    currentStreamId = chunk.Id;

                    yield return currentStreamId.ToTextStartUIMessageStreamPart();
                }

                if (currentStreamId is not null)
                {
                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = currentStreamId,
                        Delta = content
                    };
                }
            }

            if (currentStreamId is not null)
            {
                foreach (var searchResult in chunk?.SearchResults?.Where(t => !sources.Contains(t.Url)) ?? [])
                {
                    sources.Add(searchResult.Url);

                    yield return searchResult.ToSourceUIPart();
                }

                foreach (var searchResult in chunk?.Videos?.Where(t => !sources.Contains(t.Url)) ?? [])
                {
                    sources.Add(searchResult.Url);

                    yield return searchResult.ToSourceUIPart();
                }
            }

            if (!string.IsNullOrEmpty(firstChoice?.FinishReason))
            {
                if (currentStreamId is not null && chunk?.Id is not null)
                {
                    yield return currentStreamId.ToTextEndUIMessageStreamPart();

                    currentStreamId = null;
                }

                var outputTokens = chunk?.Usage?.CompletionTokens ?? 0;
                var inputTokens = chunk?.Usage?.PromptTokens ?? 0;
                var totalTokens = chunk?.Usage?.TotalTokens ?? 0;

                // Build the extra metadata only if needed
                Dictionary<string, object>? extraMetadata = null;
                var searchContextSize = chunk?.Usage?.SearchContextSize;
                var citationTokens = chunk?.Usage?.CitationTokens;

                // Only create the dictionary if there's at least one non-null value
                if (searchContextSize != null || citationTokens != null)
                {
                    extraMetadata = [];
                    if (searchContextSize != null)
                        extraMetadata["search_context_size"] = searchContextSize;
                    if (citationTokens != null)
                        extraMetadata["citation_tokens"] = citationTokens;
                }

                yield return firstChoice.FinishReason.ToFinishUIPart(
                    chatRequest.Model!.ToModelId(GetIdentifier()),
                    outputTokens,
                    inputTokens,
                    totalTokens,
                    chatRequest.Temperature,
                    reasoningTokens: chunk?.Usage?.ReasoningTokens,
                    extraMetadata: extraMetadata
                );
            }
        }
    }

    private async IAsyncEnumerable<UIMessagePart> MapPerplexityBeforeFinishAsync(
    Responses.ResponseResult responseResult,
    string providerId,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (responseResult?.Output is not IEnumerable<object> output)
            yield break;

        foreach (var obj in output)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (obj is not JsonElement item || item.ValueKind != JsonValueKind.Object)
                continue;

            if (!item.TryGetProperty("type", out var typeEl) ||
                !string.Equals(typeEl.GetString(), "search_results", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("results", out var resultsEl) ||
                resultsEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var result in resultsEl.EnumerateArray())
            {
                var url = result.TryGetProperty("url", out var urlEl)
                    ? urlEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var title = result.TryGetProperty("title", out var titleEl)
                    ? titleEl.GetString()
                    : url;

                var id = result.TryGetProperty("id", out var idEl)
                    ? idEl.ToString()
                    : url;

                var date = result.TryGetProperty("date", out var dateEl)
                              ? dateEl.GetString()
                              : null;

                var lastUpdated = result.TryGetProperty("last_updated", out var lastUpdatedEl)
                    ? lastUpdatedEl.GetString()
                    : null;

                var snippet = result.TryGetProperty("snippet", out var snippetEl)
                    ? snippetEl.GetString()
                    : null;

                yield return new SourceUIPart
                {
                    Url = url!,
                    Title = title!,
                    SourceId = id!,
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [providerId] = new Dictionary<string, object>
                        {
                            ["date"] = date ?? "",
                            ["lastUpdated"] = lastUpdated ?? "",
                            ["snippet"] = snippet ?? ""
                        }
                    }
                };
            }
        }

        await Task.CompletedTask;
    }

    private FinishUIPart CreatePerplexityFinishPart(ResponseResult response)
    {
        var usage = JsonSerializer.SerializeToElement(response.Usage, JsonSerializerOptions.Web);
        Dictionary<string, object>? extraMetadata = null;

        var searchContextSize = TryGetString(usage, "search_context_size");
        var citationTokens = TryGetInt32(usage, "citation_tokens");
        var gatewayCost = TryGetPerplexityTotalCost(usage);

        if (!string.IsNullOrWhiteSpace(searchContextSize) || citationTokens.HasValue || gatewayCost.HasValue)
        {
            extraMetadata = [];

            if (!string.IsNullOrWhiteSpace(searchContextSize))
                extraMetadata["search_context_size"] = searchContextSize;

            if (citationTokens.HasValue)
                extraMetadata["citation_tokens"] = citationTokens.Value;

            if (gatewayCost.HasValue)
            {
                extraMetadata["gateway"] = new Dictionary<string, object>
                {
                    ["cost"] = gatewayCost.Value
                };
            }
        }

        return "stop".ToFinishUIPart(
            response.Model.ToModelId(GetIdentifier()),
            TryGetInt32(usage, "output_tokens") ?? 0,
            TryGetInt32(usage, "input_tokens") ?? 0,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage) ?? 0,
            response.Temperature,
            reasoningTokens: TryGetInt32(usage, "reasoning_tokens"),
            extraMetadata: extraMetadata);
    }

    private async IAsyncEnumerable<UIMessagePart> MapPerplexityOutputItemDoneAsync(
      ResponseOutputItemDone outputItemDone,
      string providerId,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.Equals(outputItemDone.Item.Type, "search_results", StringComparison.OrdinalIgnoreCase))
        {
            var id = Guid.NewGuid().ToString();
            JsonElement? queries = null;

            if (outputItemDone.Item.AdditionalProperties?.TryGetValue("queries", out var q) == true &&
                q.ValueKind == JsonValueKind.Array)
            {
                queries = q;
            }

            yield return new ToolCallPart()
            {
                ToolCallId = id,
                ToolName = "search",
                ProviderExecuted = true,
                Title = "Search",
                Input = new
                {
                    queries
                }

            };

            JsonElement? results = null;

            if (outputItemDone.Item.AdditionalProperties?.TryGetValue("results", out var r) == true &&
                r.ValueKind == JsonValueKind.Array)
            {
                results = r;
            }

            yield return new ToolOutputAvailablePart()
            {
                ToolCallId = id,
                ProviderExecuted = true,
                Output = new ModelContextProtocol.Protocol.CallToolResult()
                {
                    StructuredContent = results
                }

            };

            yield break;

        }

        await Task.CompletedTask;
    }

}

