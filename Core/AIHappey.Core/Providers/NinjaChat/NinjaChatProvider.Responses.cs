using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return ExecuteNativeSearchResponsesAsync(options, cancellationToken);

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return ExecuteNativeSearchResponsesStreamingAsync(options, cancellationToken);

        throw new NotImplementedException();
    }


    private async Task<ResponseResult> ExecuteNativeSearchResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(options), cancellationToken);
        return ToNativeSearchResponseResult(execution, options, status: "completed");
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteNativeSearchResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(options), cancellationToken);
        var response = ToNativeSearchResponseResult(execution, options, status: "completed");
        var itemId = $"msg_{execution.Id}";
        var sequence = 1;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = ToNativeSearchResponseResult(execution, options, status: "in_progress")
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = ToNativeSearchResponseResult(execution, options, status: "in_progress")
        };

        yield return new ResponseOutputItemAdded
        {
            SequenceNumber = sequence++,
            OutputIndex = 0,
            Item = new ResponseStreamItem
            {
                Id = itemId,
                Type = "message",
                Status = "in_progress",
                Role = "assistant"
            }
        };

        var annotations = BuildNativeSearchAnnotations(execution.Response).ToList();
        yield return new ResponseContentPartAdded
        {
            SequenceNumber = sequence++,
            OutputIndex = 0,
            ContentIndex = 0,
            ItemId = itemId,
            Part = new ResponseStreamContentPart
            {
                Type = "output_text",
                Text = null,
                Annotations = annotations
            }
        };

        for (var i = 0; i < annotations.Count; i++)
        {
            yield return new ResponseOutputTextAnnotationAdded
            {
                SequenceNumber = sequence++,
                OutputIndex = 0,
                ContentIndex = 0,
                ItemId = itemId,
                AnnotationIndex = i,
                Annotation = annotations[i]
            };
        }

        foreach (var chunk in ChunkText(execution.Text))
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
            Text = execution.Text
        };

        yield return new ResponseContentPartDone
        {
            SequenceNumber = sequence++,
            OutputIndex = 0,
            ContentIndex = 0,
            ItemId = itemId,
            Part = new ResponseStreamContentPart
            {
                Type = "output_text",
                Text = execution.Text,
                Annotations = annotations
            }
        };

        yield return new ResponseOutputItemDone
        {
            SequenceNumber = sequence++,
            OutputIndex = 0,
            Item = new ResponseStreamItem
            {
                Id = itemId,
                Type = "message",
                Status = "completed",
                Role = "assistant",
                Content =
                [
                    new ResponseStreamContentPart
                    {
                        Type = "output_text",
                        Text = execution.Text,
                        Annotations = annotations
                    }
                ]
            }
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = response
        };
    }


    private ResponseResult ToNativeSearchResponseResult(
        NinjaChatSearchExecutionResult execution,
        ResponseRequest options,
        string status)
    {
        var annotations = BuildNativeSearchAnnotationObjects(execution.Response).ToArray();
        var metadata = MergeMetadata(options.Metadata, execution.Response);

        return new ResponseResult
        {
            Id = execution.Id,
            Object = "response",
            CreatedAt = execution.CreatedAt,
            CompletedAt = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                ? execution.CreatedAt
                : null,
            Status = status,
            Model = options.Model ?? NativeSearchModelId,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            ParallelToolCalls = options.ParallelToolCalls,
            Text = options.Text,
            Metadata = metadata,
            Output =
            [
                new
                {
                    type = "message",
                    id = $"msg_{execution.Id}",
                    status,
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = execution.Text,
                            annotations
                        }
                    }
                }
            ]
        };
    }


    private NinjaChatSearchRequest BuildNativeSearchRequest(ResponseRequest options)
        => BuildNativeSearchRequest(
            query: BuildPromptFromResponseRequest(options),
            passthrough: GetRawProviderPassthroughFromResponseRequest(options));

    private static IEnumerable<ResponseStreamAnnotation> BuildNativeSearchAnnotations(NinjaChatSearchResponse response)
           => response.Sources
               .Where(s => !string.IsNullOrWhiteSpace(s.Url))
               .Select((source, index) => new ResponseStreamAnnotation
               {
                   Type = "url_citation",
                   AdditionalProperties = new Dictionary<string, JsonElement>
                   {
                       ["url"] = JsonSerializer.SerializeToElement(source.Url, NinjaChatJson),
                       ["title"] = JsonSerializer.SerializeToElement(source.Title, NinjaChatJson),
                       ["source_index"] = JsonSerializer.SerializeToElement(index, NinjaChatJson),
                       ["content"] = JsonSerializer.SerializeToElement(source.Content, NinjaChatJson)
                   }
               });

}
