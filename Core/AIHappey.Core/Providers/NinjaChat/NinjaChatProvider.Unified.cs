using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedEnsembleAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = request.ToChatCompletionOptions(GetIdentifier());
        options.Stream = false;
        options.Store ??= false;

        var completion = await CompleteChatAsync(options, cancellationToken);
        var rawResponse = JsonSerializer.SerializeToElement(completion, NinjaChatJson);
        var streamId = string.IsNullOrWhiteSpace(completion.Id)
            ? $"chatcmpl_{Guid.NewGuid():N}"
            : completion.Id;
        var model = string.IsNullOrWhiteSpace(completion.Model) ? options.Model : completion.Model;
        var timestamp = completion.Created > 0
            ? DateTimeOffset.FromUnixTimeSeconds(completion.Created)
            : DateTimeOffset.UtcNow;
        var created = completion.Created > 0
            ? completion.Created
            : timestamp.ToUnixTimeSeconds();
        var providerMetadata = BuildEnsembleProviderMetadata(rawResponse);
        var text = ExtractAssistantMessageText(completion);
        var finishReason = ExtractFinishReason(completion) ?? "stop";
        var inputTokens = ExtractUsageInt(completion.Usage, "prompt_tokens", "promptTokens", "inputTokens", "input_tokens");
        var outputTokens = ExtractUsageInt(completion.Usage, "completion_tokens", "completionTokens", "outputTokens", "output_tokens");
        var totalTokens = ExtractUsageInt(completion.Usage, "total_tokens", "totalTokens", "total_tokens");

        if (!string.IsNullOrWhiteSpace(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSyntheticEnsembleTextEvent(
                type: "text-start",
                streamId: streamId,
                timestamp: timestamp,
                rawResponse: rawResponse,
                rawChunk: CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleStartChoice(),
                    usage: null,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata: providerMetadata,
                delta: null);

            foreach (var chunk in ChunkText(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateSyntheticEnsembleTextEvent(
                    type: "text-delta",
                    streamId: streamId,
                    timestamp: timestamp,
                    rawResponse: rawResponse,
                    rawChunk: CreateSyntheticEnsembleChunk(
                        streamId,
                        created,
                        model,
                        CreateEnsembleTextDeltaChoice(chunk),
                        usage: null,
                        additionalProperties: completion.AdditionalProperties),
                    providerMetadata: providerMetadata,
                    delta: chunk);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSyntheticEnsembleTextEvent(
                type: "text-end",
                streamId: streamId,
                timestamp: timestamp,
                rawResponse: rawResponse,
                rawChunk: CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleTextEndChoice(),
                    usage: null,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata: providerMetadata,
                delta: null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = BuildEnsembleStreamMetadata(
                rawResponse,
                CreateSyntheticEnsembleChunk(
                    streamId,
                    created,
                    model,
                    CreateEnsembleFinishChoice(finishReason),
                    usage: completion.Usage,
                    additionalProperties: completion.AdditionalProperties),
                providerMetadata),
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = streamId,
                Timestamp = timestamp,
                Data = new AIFinishEventData
                {
                    FinishReason = finishReason,
                    Model = model,
                    CompletedAt = created,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    Response = rawResponse,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        model: model ?? string.Empty,
                        timestamp: timestamp,
                        usage: completion.Usage,
                        outputTokens: outputTokens,
                        inputTokens: inputTokens,
                        totalTokens: totalTokens,
                        additionalProperties: BuildEnsembleFinishAdditionalProperties(rawResponse, providerMetadata))
                }
            }
        };
    }

    private AIStreamEvent CreateSyntheticEnsembleTextEvent(
        string type,
        string streamId,
        DateTimeOffset timestamp,
        JsonElement rawResponse,
        JsonElement rawChunk,
        Dictionary<string, object?> providerMetadata,
        string? delta)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = BuildEnsembleStreamMetadata(rawResponse, rawChunk, providerMetadata),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = streamId,
                Timestamp = timestamp,
                Data = type switch
                {
                    "text-start" => new AITextStartEventData(),
                    "text-end" => new AITextEndEventData(),
                    _ => new AITextDeltaEventData { Delta = delta ?? string.Empty }
                }
            }
        };

    private Dictionary<string, object?> BuildEnsembleStreamMetadata(
        JsonElement rawResponse,
        JsonElement rawChunk,
        Dictionary<string, object?> providerMetadata)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatcompletions.response.raw"] = rawResponse.Clone(),
            ["chatcompletions.stream.raw"] = rawChunk.Clone(),
            [GetIdentifier()] = CloneProviderMetadata(providerMetadata)
        };

        foreach (var prop in rawResponse.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        foreach (var prop in rawChunk.EnumerateObject())
            metadata[$"chatcompletions.stream.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private Dictionary<string, object?> BuildEnsembleFinishAdditionalProperties(
        JsonElement rawResponse,
        Dictionary<string, object?> providerMetadata)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatcompletions.response.raw"] = rawResponse.Clone(),
            [GetIdentifier()] = CloneProviderMetadata(providerMetadata)
        };

        foreach (var prop in rawResponse.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> CloneProviderMetadata(Dictionary<string, object?> providerMetadata)
    {
        var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in providerMetadata)
            clone[item.Key] = item.Value;

        return clone;
    }

    private static Dictionary<string, object?> BuildEnsembleProviderMetadata(JsonElement rawResponse)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in rawResponse.EnumerateObject())
        {
            if (prop.Name is "id" or "object" or "created" or "model" or "choices" or "usage")
                continue;

            metadata[prop.Name] = prop.Value.Clone();
        }

        return metadata;
    }

    private static JsonElement CreateSyntheticEnsembleChunk(
        string id,
        long created,
        string? model,
        object choice,
        object? usage,
        Dictionary<string, JsonElement>? additionalProperties)
    {
        var chunk = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new[] { choice },
            ["usage"] = usage
        };

        if (additionalProperties is not null)
        {
            foreach (var property in additionalProperties)
                chunk[property.Key] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(chunk, NinjaChatJson);
    }

    private static object CreateEnsembleStartChoice()
        => new
        {
            index = 0,
            delta = new { role = "assistant" },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleTextDeltaChoice(string chunk)
        => new
        {
            index = 0,
            delta = new { content = chunk },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleTextEndChoice()
        => new
        {
            index = 0,
            delta = new { },
            finish_reason = (string?)null
        };

    private static object CreateEnsembleFinishChoice(string finishReason)
        => new
        {
            index = 0,
            delta = new { },
            finish_reason = finishReason
        };

    private static string ExtractAssistantMessageText(ChatCompletion completion)
    {
        foreach (var choice in completion.Choices)
        {
            var root = JsonSerializer.SerializeToElement(choice, NinjaChatJson);
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            var role = message.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(role)
                && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content))
                continue;

            var text = ExtractCompletionMessageText(content);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string? ExtractFinishReason(ChatCompletion completion)
    {
        foreach (var choice in completion.Choices)
        {
            var root = JsonSerializer.SerializeToElement(choice, NinjaChatJson);
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            if (!root.TryGetProperty("finish_reason", out var finishReason) || finishReason.ValueKind != JsonValueKind.String)
                continue;

            var value = finishReason.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int? ExtractUsageInt(object? usage, params string[] propertyNames)
    {
        if (usage is null)
            return null;

        JsonElement usageElement;
        try
        {
            usageElement = usage is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(usage, NinjaChatJson);
        }
        catch
        {
            return null;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!usageElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
                continue;

            if (value.TryGetInt32(out var intValue))
                return intValue;

            if (value.TryGetInt64(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue)
                return (int)longValue;
        }

        return null;
    }

    private async Task<AIResponse> ExecuteUnifiedSearchAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(request), cancellationToken);
        return ToUnifiedSearchResponse(execution, request);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedSearchAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(request), cancellationToken);
        var model = ResolveNativeSearchModel(request.Model);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(execution.CreatedAt);
        var streamId = execution.Id;
        var usage = BuildNativeSearchUsage(execution.Response);
        var response = ToUnifiedSearchResponse(execution, request);
        var metadata = BuildNativeSearchStreamMetadata(execution, request, model);
        var rawResponse = JsonSerializer.SerializeToElement(execution.Response, NinjaChatJson);

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = "text-start",
                Id = streamId,
                Timestamp = timestamp,
                Data = new AITextStartEventData()
            }
        };

        foreach (var source in execution.Response.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Url))
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = metadata,
                Event = new AIEventEnvelope
                {
                    Type = "source-url",
                    Id = streamId,
                    Timestamp = timestamp,
                    Data = new AISourceUrlEventData
                    {
                        SourceId = source.Url,
                        Url = source.Url,
                        Title = string.IsNullOrWhiteSpace(source.Title) ? source.Url : source.Title,
                        Type = "web_search_result_location",
                        ProviderMetadata = CreateNativeSearchSourceProviderMetadata(source)
                    }
                }
            };
        }

        if (execution.Response.Images.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = metadata,
                Event = new AIEventEnvelope
                {
                    Type = "data-ninjachat.images",
                    Id = streamId,
                    Timestamp = timestamp,
                    Data = new AIDataEventData
                    {
                        Id = streamId,
                        Data = BuildNativeSearchImageDtos(execution.Response).ToList()
                    }
                }
            };
        }

        foreach (var downloadedImage in execution.DownloadedImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = metadata,
                Event = new AIEventEnvelope
                {
                    Type = "file",
                    Id = streamId,
                    Timestamp = timestamp,
                    Data = new AIFileEventData
                    {
                        MediaType = downloadedImage.MediaType,
                        Url = downloadedImage.DataUrl,
                        Filename = downloadedImage.Filename,
                        ProviderMetadata = CreateNativeSearchImageProviderMetadata(downloadedImage)
                    }
                }
            };
        }

        if (execution.Response.FollowUpQuestions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = metadata,
                Event = new AIEventEnvelope
                {
                    Type = "data-ninjachat.follow-up-questions",
                    Id = streamId,
                    Timestamp = timestamp,
                    Data = new AIDataEventData
                    {
                        Id = streamId,
                        Data = execution.Response.FollowUpQuestions.ToList()
                    }
                }
            };
        }

        foreach (var chunk in ChunkText(execution.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Metadata = metadata,
                Event = new AIEventEnvelope
                {
                    Type = "text-delta",
                    Id = streamId,
                    Timestamp = timestamp,
                    Data = new AITextDeltaEventData
                    {
                        Delta = chunk
                    }
                }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = "text-end",
                Id = streamId,
                Timestamp = timestamp,
                Data = new AITextEndEventData()
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = streamId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = model.ToModelId(GetIdentifier()),
                    CompletedAt = execution.CreatedAt,
                    InputTokens = 0,
                    OutputTokens = 0,
                    TotalTokens = 0,
                    Response = rawResponse,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        model,
                        timestamp,
                        usage: usage,
                        inputTokens: 0,
                        outputTokens: 0,
                        totalTokens: 0,
                        temperature: request.Temperature,
                        additionalProperties: BuildNativeSearchFinishAdditionalProperties(execution))
                }
            }
        };
    }

    private AIResponse ToUnifiedSearchResponse(NinjaChatSearchExecutionResult execution, AIRequest request)
    {
        var model = ResolveNativeSearchModel(request.Model);
        var items = new List<AIOutputItem>
        {
            CreateNativeSearchMessageOutputItem(execution)
        };

        items.AddRange(execution.Response.Sources.Select(CreateNativeSearchSourceOutputItem));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Usage = BuildNativeSearchUsage(execution.Response),
            Output = new AIOutput
            {
                Items = items,
                Metadata = BuildNativeSearchOutputMetadata(execution)
            },
            Metadata = BuildNativeSearchUnifiedMetadata(execution, request, model)
        };
    }

    private NinjaChatSearchRequest BuildNativeSearchRequest(AIRequest request)
        => BuildNativeSearchRequest(
            query: BuildPromptFromUnifiedRequest(request),
            passthrough: GetRawProviderPassthroughFromUnifiedRequest(request));

    private Dictionary<string, object?>? GetRawProviderPassthroughFromUnifiedRequest(AIRequest request)
    {
        var raw = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        return raw.ValueKind == JsonValueKind.Object
            ? JsonElementObjectToDictionary(raw)
            : null;
    }

    private static string BuildPromptFromUnifiedRequest(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var items = request.Input?.Items?.ToList() ?? [];
        if (items.Count == 0)
            return request.Instructions ?? string.Empty;

        var system = new List<string>();
        var lines = new List<string>();

        foreach (var item in items)
        {
            var role = (item.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = string.Join(
                "\n",
                (item.Content ?? [])
                    .OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        var prompt = string.Join("\n\n", lines);
        return string.IsNullOrWhiteSpace(prompt)
            ? request.Instructions ?? string.Empty
            : prompt;
    }

    private AIOutputItem CreateNativeSearchMessageOutputItem(NinjaChatSearchExecutionResult execution)
    {
        var citations = BuildNativeSearchMessageCitations(execution.Response).ToList();
        var rawBlock = new MessageContentBlock
        {
            Type = "text",
            Text = execution.Text,
            Citations = citations.Count > 0 ? citations : null
        };

        var content = new List<AIContentPart>
        {
            new AITextContentPart
            {
                Type = "text",
                Text = execution.Text,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.block.raw"] = JsonSerializer.SerializeToElement(rawBlock, NinjaChatJson),
                    ["responses.type"] = "output_text"
                }
            }
        };

        content.AddRange(execution.DownloadedImages.Select(CreateNativeSearchImageContentPart));

        return new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content = content,
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.message.sources"] = BuildNativeSearchSourceDtos(execution.Response).ToList(),
                ["chatcompletions.choice.finish_reason"] = "stop",
                ["ninjachat.query"] = execution.Response.Query,
                ["ninjachat.answer"] = execution.Response.Answer,
                ["ninjachat.images"] = JsonSerializer.SerializeToElement(execution.Response.Images, NinjaChatJson),
                ["ninjachat.downloaded_images"] = BuildNativeSearchDownloadedImageDtos(execution).ToList(),
                ["ninjachat.follow_up_questions"] = JsonSerializer.SerializeToElement(execution.Response.FollowUpQuestions, NinjaChatJson)
            }
        };
    }

    private static AIFileContentPart CreateNativeSearchImageContentPart(NinjaChatDownloadedImage image)
        => new()
        {
            Type = "file",
            MediaType = image.MediaType,
            Filename = image.Filename,
            Data = image.DataUrl,
            Metadata = new Dictionary<string, object?>
            {
                ["ninjachat.image.origin_url"] = image.OriginUrl,
                ["ninjachat.image.description"] = image.Description
            }
        };

    private static AIOutputItem CreateNativeSearchSourceOutputItem(NinjaChatSearchSource source)
        => new()
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = string.IsNullOrWhiteSpace(source.Title) ? source.Url ?? string.Empty : source.Title
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = source.Url,
                ["chatcompletions.source.title"] = source.Title,
                ["messages.source.url"] = source.Url,
                ["messages.source.title"] = source.Title,
                ["messages.source.type"] = "web_search_result_location",
                ["ninjachat.source.content"] = source.Content,
                ["ninjachat.source.published_date"] = source.PublishedDate,
                ["ninjachat.source.raw"] = JsonSerializer.SerializeToElement(source, NinjaChatJson)
            }
        };

    private static IEnumerable<MessageCitation> BuildNativeSearchMessageCitations(NinjaChatSearchResponse response)
        => response.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Select((source, index) => new MessageCitation
            {
                Type = "web_search_result_location",
                Url = source.Url,
                Title = source.Title,
                EncryptedIndex = source.Url,
                SearchResultIndex = index,
                Source = "ninjachat.search"
            });

    private static IEnumerable<object> BuildNativeSearchSourceDtos(NinjaChatSearchResponse response)
        => response.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => new Dictionary<string, object?>
            {
                ["url"] = source.Url,
                ["title"] = source.Title,
                ["content"] = source.Content,
                ["published_date"] = source.PublishedDate
            });

    private static IEnumerable<object> BuildNativeSearchImageDtos(NinjaChatSearchResponse response)
        => response.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .Select(image => new Dictionary<string, object?>
            {
                ["url"] = image.Url,
                ["description"] = image.Description
            });

    private static IEnumerable<object> BuildNativeSearchDownloadedImageDtos(NinjaChatSearchExecutionResult execution)
        => execution.DownloadedImages.Select(image => new Dictionary<string, object?>
        {
            ["data_url"] = image.DataUrl,
            ["media_type"] = image.MediaType,
            ["filename"] = image.Filename,
            ["origin_url"] = image.OriginUrl,
            ["description"] = image.Description
        });

    private Dictionary<string, object?> BuildNativeSearchUnifiedMetadata(
        NinjaChatSearchExecutionResult execution,
        AIRequest request,
        string model)
    {
        var metadata = request.Metadata is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(request.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["ninjachat.request.query"] = execution.Request.Query;
        metadata["ninjachat.query"] = execution.Response.Query ?? execution.Request.Query;
        metadata["ninjachat.answer"] = execution.Response.Answer;
        metadata["ninjachat.sources"] = JsonSerializer.SerializeToElement(execution.Response.Sources, NinjaChatJson);
        metadata["ninjachat.images"] = JsonSerializer.SerializeToElement(execution.Response.Images, NinjaChatJson);
        metadata["ninjachat.downloaded_images"] = JsonSerializer.SerializeToElement(BuildNativeSearchDownloadedImageDtos(execution).ToList(), NinjaChatJson);
        metadata["ninjachat.follow_up_questions"] = JsonSerializer.SerializeToElement(execution.Response.FollowUpQuestions, NinjaChatJson);
        metadata["ninjachat.cost"] = execution.Response.Cost is null ? null : JsonSerializer.SerializeToElement(execution.Response.Cost, NinjaChatJson);
        metadata["ninjachat.search_metadata"] = execution.Response.Metadata is null ? null : JsonSerializer.SerializeToElement(execution.Response.Metadata, NinjaChatJson);
        metadata["ninjachat.response.raw"] = JsonSerializer.SerializeToElement(execution.Response, NinjaChatJson);

        metadata["messages.response.id"] = execution.Id;
        metadata["messages.response.model"] = model;
        metadata["messages.response.role"] = "assistant";
        metadata["messages.response.type"] = "message";
        metadata["messages.response.stop_reason"] = "end_turn";

        metadata["chatcompletions.response.id"] = execution.Id;
        metadata["chatcompletions.response.object"] = "chat.completion";
        metadata["chatcompletions.response.created"] = execution.CreatedAt;
        metadata["chatcompletions.response.model"] = model;

        metadata["responses.id"] = execution.Id;
        metadata["responses.object"] = "response";
        metadata["responses.created_at"] = execution.CreatedAt;
        metadata["responses.completed_at"] = execution.CreatedAt;
        metadata["responses.model"] = model;

        return metadata;
    }

    private Dictionary<string, object?> BuildNativeSearchStreamMetadata(
        NinjaChatSearchExecutionResult execution,
        AIRequest request,
        string model)
    {
        var metadata = BuildNativeSearchUnifiedMetadata(execution, request, model);
        metadata["chatcompletions.stream.id"] = execution.Id;
        metadata["chatcompletions.stream.object"] = "chat.completion.chunk";
        metadata["chatcompletions.stream.created"] = execution.CreatedAt;
        metadata["chatcompletions.stream.model"] = model;
        metadata["responses.response.id"] = execution.Id;
        metadata["responses.response.model"] = model;
        return metadata;
    }

    private static Dictionary<string, object?> BuildNativeSearchOutputMetadata(NinjaChatSearchExecutionResult execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["ninjachat.query"] = execution.Response.Query,
            ["ninjachat.images"] = JsonSerializer.SerializeToElement(execution.Response.Images, NinjaChatJson),
            ["ninjachat.downloaded_images"] = JsonSerializer.SerializeToElement(BuildNativeSearchDownloadedImageDtos(execution).ToList(), NinjaChatJson),
            ["ninjachat.follow_up_questions"] = JsonSerializer.SerializeToElement(execution.Response.FollowUpQuestions, NinjaChatJson),
            ["ninjachat.search_metadata"] = execution.Response.Metadata is null ? null : JsonSerializer.SerializeToElement(execution.Response.Metadata, NinjaChatJson)
        };

    private Dictionary<string, Dictionary<string, object>> CreateNativeSearchImageProviderMetadata(NinjaChatDownloadedImage image)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [GetIdentifier()] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["origin_url"] = image.OriginUrl,
                ["description"] = image.Description ?? string.Empty,
                ["filename"] = image.Filename
            }
        };

    private static Dictionary<string, object?> BuildNativeSearchUsage(NinjaChatSearchResponse response)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["input_tokens"] = 0,
            ["output_tokens"] = 0,
            ["cost"] = response.Cost?.ThisRequest,
            ["results_count"] = response.Metadata?.ResultsCount,
            ["latency_ms"] = response.Metadata?.LatencyMs,
            ["group"] = response.Metadata?.Group,
            ["search_depth"] = response.Metadata?.SearchDepth
        };

    private Dictionary<string, Dictionary<string, object>>? CreateNativeSearchSourceProviderMetadata(NinjaChatSearchSource source)
    {
        var scoped = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(source.Content))
            scoped["content"] = source.Content!;

        if (!string.IsNullOrWhiteSpace(source.PublishedDate))
            scoped["published_date"] = source.PublishedDate!;

        return scoped.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase)
            {
                [GetIdentifier()] = scoped
            };
    }

    private static Dictionary<string, object?> BuildNativeSearchFinishAdditionalProperties(NinjaChatSearchExecutionResult execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(NinjaChat).ToLowerInvariant()] = new Dictionary<string, object?>
            {
                ["query"] = execution.Response.Query,
                ["answer"] = execution.Response.Answer,
                ["sources"] = JsonSerializer.SerializeToElement(execution.Response.Sources, NinjaChatJson),
                ["images"] = JsonSerializer.SerializeToElement(execution.Response.Images, NinjaChatJson),
                ["downloaded_images"] = JsonSerializer.SerializeToElement(BuildNativeSearchDownloadedImageDtos(execution).ToList(), NinjaChatJson),
                ["follow_up_questions"] = JsonSerializer.SerializeToElement(execution.Response.FollowUpQuestions, NinjaChatJson),
                ["cost"] = execution.Response.Cost is null ? null : JsonSerializer.SerializeToElement(execution.Response.Cost, NinjaChatJson),
                ["search_metadata"] = execution.Response.Metadata is null ? null : JsonSerializer.SerializeToElement(execution.Response.Metadata, NinjaChatJson)
            }
        };

    private static string ResolveNativeSearchModel(string? model)
        => string.IsNullOrWhiteSpace(model)
            ? NativeSearchModelId
            : model.Trim();
}
