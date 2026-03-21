using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    private async Task<ResponseResult> ResponsesCoreAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("You.com requires non-empty responses input.");

        var metadata = ReadResponseMetadata(options);

        if (IsResearchModel(options.Model))
        {
            if (options.Tools?.Any() == true)
                throw new NotSupportedException("You.com research models do not support tools on the responses surface. Use agent models for grounded agent behavior.");

            var result = await ExecuteResearchAsync(options.Model!, prompt, options.Text, metadata.ResearchEffort, cancellationToken);
            return ToResponseResult(result, options);
        }

        if (!IsAgentModel(options.Model))
            throw new NotSupportedException($"Unsupported You.com responses model '{options.Model}'.");

        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var text = new StringBuilder();
        var finishReason = "error";
        var runtimeMs = (long?)null;
        var sources = new Dictionary<string, YouComSourceInfo>(StringComparer.OrdinalIgnoreCase);

        await foreach (var evt in StreamAgentEventsAsync(options.Model!, prompt, options.Text, options.Tools?.Any() == true, metadata, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Delta))
                text.Append(evt.Delta);

            foreach (var source in evt.Sources ?? [])
                sources[source.Url] = source;

            if (evt.Type == "response.done")
            {
                finishReason = evt.Finished == false ? "error" : "stop";
                runtimeMs = evt.RuntimeMs;
            }
        }

        return ToResponseResult(new YouComExecutionResult
        {
            Id = responseId,
            Model = options.Model!,
            Endpoint = "agents.runs",
            Text = text.ToString(),
            Sources = sources.Values.ToList(),
            FinishReason = finishReason,
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RuntimeMs = runtimeMs,
            Error = finishReason == "stop" ? null : "You.com agent run did not finish successfully."
        }, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesCoreStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("You.com requires non-empty responses input.");

        var metadata = ReadResponseMetadata(options);
        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var itemId = $"msg_{responseId}";
        var sequence = 1;

        var initial = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = options.Model!,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = initial
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = initial
        };

        if (IsResearchModel(options.Model))
        {
            if (options.Tools?.Any() == true)
                throw new NotSupportedException("You.com research models do not support tools on the responses surface. Use agent models for grounded agent behavior.");

            var result = await ExecuteResearchAsync(options.Model!, prompt, options.Text, metadata.ResearchEffort, cancellationToken);
            foreach (var chunk in ChunkText(result.Text))
            {
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = chunk
                };
            }

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Text = result.Text
            };

            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = ToResponseResult(new YouComExecutionResult
                {
                    Id = responseId,
                    Model = options.Model!,
                    Endpoint = result.Endpoint,
                    Text = result.Text,
                    Sources = result.Sources,
                    FinishReason = "stop",
                    CreatedAt = createdAt,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }, options)
            };

            yield break;
        }

        if (!IsAgentModel(options.Model))
            throw new NotSupportedException($"Unsupported You.com responses model '{options.Model}'.");

        var text = new StringBuilder();
        var sources = new Dictionary<string, YouComSourceInfo>(StringComparer.OrdinalIgnoreCase);
        var finishReason = "error";
        long? runtimeMs = null;

        await foreach (var evt in StreamAgentEventsAsync(options.Model!, prompt, options.Text, options.Tools?.Any() == true, metadata, cancellationToken))
        {
            foreach (var source in evt.Sources ?? [])
                sources[source.Url] = source;

            if (!string.IsNullOrWhiteSpace(evt.Delta))
            {
                text.Append(evt.Delta);
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = evt.Delta!
                };
            }

            if (evt.Type == "response.done")
            {
                finishReason = evt.Finished == false ? "error" : "stop";
                runtimeMs = evt.RuntimeMs;
            }
        }

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = text.ToString()
        };

        var final = ToResponseResult(new YouComExecutionResult
        {
            Id = responseId,
            Model = options.Model!,
            Endpoint = "agents.runs",
            Text = text.ToString(),
            Sources = sources.Values.ToList(),
            FinishReason = finishReason,
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RuntimeMs = runtimeMs,
            Error = finishReason == "stop" ? null : "You.com agent run did not finish successfully."
        }, options);

        if (finishReason == "stop")
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = final
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = final
            };
        }
    }
}
