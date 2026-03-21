using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AI21;

public sealed partial class AI21Provider
{
    private static readonly JsonSerializerOptions MaestroJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private const string MaestroModelId = "maestro";
    private const string MaestroRunsPath = "v1/maestro/runs";
    private const int MaestroPollingDelayMs = 1000;

    private static bool IsMaestroModel(string? model)
        => string.Equals(model, MaestroModelId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "ai21/maestro", StringComparison.OrdinalIgnoreCase);

    private async Task<Ai21MaestroRun> CreateMaestroRunAsync(
        Ai21MaestroCreateRunRequest payload,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(MaestroRunsPath, payload, MaestroJson, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AI21 Maestro create run error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

        return JsonSerializer.Deserialize<Ai21MaestroRun>(raw, MaestroJson)
            ?? throw new InvalidOperationException("AI21 Maestro returned an empty create run response.");
    }

    private async Task<Ai21MaestroRun> GetMaestroRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"{MaestroRunsPath}/{Uri.EscapeDataString(runId)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AI21 Maestro retrieve run error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

        return JsonSerializer.Deserialize<Ai21MaestroRun>(raw, MaestroJson)
            ?? throw new InvalidOperationException("AI21 Maestro returned an empty retrieve run response.");
    }

    private async Task<Ai21MaestroRun> WaitForMaestroRunCompletionAsync(string runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var run = await GetMaestroRunAsync(runId, cancellationToken);
            if (!string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                return run;

            await Task.Delay(MaestroPollingDelayMs, cancellationToken);
        }
    }

    private static string ExtractMaestroResultText(Ai21MaestroRun run)
    {
        if (run.Result is null)
            return string.Empty;

        return run.Result.Value.ValueKind switch
        {
            JsonValueKind.String => run.Result.Value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => run.Result.Value.GetRawText()
        };
    }

    private static object? ExtractMaestroStructuredResult(Ai21MaestroRun run)
    {
        if (run.Result is null)
            return null;

        return run.Result.Value.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Deserialize<object>(run.Result.Value.GetRawText(), MaestroJson),
            _ => null
        };
    }

    private static object BuildMaestroUsage()
        => new
        {
            prompt_tokens = 0,
            completion_tokens = 0,
            total_tokens = 0
        };

    private static string GetMaestroFailureMessage(Ai21MaestroRun run)
        => run.Error?.Message
        ?? run.RequirementsResult?.FinishReason
        ?? $"AI21 Maestro run '{run.Id}' failed.";

    private static long ToUnixTimeSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static DateTimeOffset GetCreatedAt(Ai21MaestroRun run) => DateTimeOffset.UtcNow;

    private static IEnumerable<string> ChunkText(string text, int chunkSize = 180)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }

    private static SourceUIPart ToSourcePart(Ai21MaestroWebSearchSource source)
    {
        var url = source.Url ?? Guid.NewGuid().ToString("n");
        Dictionary<string, object>? providerMetadata = null;

        if (!string.IsNullOrWhiteSpace(source.Text) || source.Score is not null)
        {
            providerMetadata = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(source.Text))
                providerMetadata["text"] = source.Text!;
            if (source.Score is not null)
                providerMetadata["score"] = source.Score.Value;
        }

        return new SourceUIPart
        {
            SourceId = url,
            Url = url,
            Title = url,
            ProviderMetadata = providerMetadata?.ToProviderMetadata("ai21")
        };
    }

    private static SourceDocumentPart ToDocumentPart(Ai21MaestroFileSearchSource source)
    {
        Dictionary<string, object>? providerMetadata = null;
        if (!string.IsNullOrWhiteSpace(source.Text) || source.Score is not null || source.Order is not null)
        {
            providerMetadata = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(source.Text))
                providerMetadata["text"] = source.Text!;
            if (source.Score is not null)
                providerMetadata["score"] = source.Score.Value;
            if (source.Order is not null)
                providerMetadata["order"] = source.Order.Value;
        }

        return new SourceDocumentPart
        {
            SourceId = source.FileId ?? source.FileName ?? Guid.NewGuid().ToString("n"),
            Filename = source.FileName,
            Title = source.FileName ?? source.FileId ?? "document",
            MediaType = "application/octet-stream",
            ProviderMetadata = providerMetadata
        };
    }

    private static ToolCallStreamingStartPart ToToolStartPart(Ai21MaestroToolCall toolCall)
        => new()
        {
            ToolCallId = BuildToolCallId(toolCall),
            ToolName = toolCall.ToolName ?? toolCall.ToolType ?? "tool",
            ProviderExecuted = true,
            Title = toolCall.ToolName ?? toolCall.ToolType
        };

    private static ToolCallDeltaPart? ToToolDeltaPart(Ai21MaestroToolCall toolCall)
    {
        var text = toolCall.Parameters?.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => toolCall.Parameters.Value.GetRawText(),
            JsonValueKind.String => toolCall.Parameters.Value.GetString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new ToolCallDeltaPart
        {
            ToolCallId = BuildToolCallId(toolCall),
            InputTextDelta = text
        };
    }

    private static ToolOutputAvailablePart ToToolOutputPart(Ai21MaestroToolCall toolCall)
    {
        object output = toolCall.Response?.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Deserialize<object>(toolCall.Response.Value.GetRawText(), MaestroJson) ?? new { },
            JsonValueKind.String => toolCall.Response.Value.GetString() ?? string.Empty,
            _ => new
            {
                isError = false,
                content = "Provider-side tool execution completed."
            }
        };

        return new ToolOutputAvailablePart
        {
            ToolCallId = BuildToolCallId(toolCall),
            ProviderExecuted = true,
            Output = output
        };
    }

    private static string BuildToolCallId(Ai21MaestroToolCall toolCall)
        => !string.IsNullOrWhiteSpace(toolCall.ToolName)
            ? $"tool_{toolCall.ToolName}_{Math.Abs((toolCall.Parameters?.GetRawText() ?? string.Empty).GetHashCode())}"
            : $"tool_{Guid.NewGuid():N}";

    private ResponseResult ToMaestroResponseResult(Ai21MaestroRun run, ResponseRequest options)
    {
        var createdAt = ToUnixTimeSeconds(GetCreatedAt(run));
        var completedAt = string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            : (long?)null;

        var outputText = ExtractMaestroResultText(run);
        var output = new List<object>
        {
            new
            {
                id = $"msg_{run.Id}",
                type = "message",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = outputText
                    }
                }
            }
        };

        var metadata = new Dictionary<string, object?>();
        if (options.Metadata is not null)
        {
            foreach (var kv in options.Metadata)
                metadata[kv.Key] = kv.Value;
        }

        if (run.RequirementsResult is not null)
            metadata["requirements_result"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(run.RequirementsResult, MaestroJson), MaestroJson);
        if (run.DataSources is not null)
            metadata["data_sources"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(run.DataSources, MaestroJson), MaestroJson);

        return new ResponseResult
        {
            Id = run.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "in_progress",
            Model = options.Model ?? MaestroModelId,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            ParallelToolCalls = options.ParallelToolCalls,
            Text = options.Text,
            Metadata = metadata.Count == 0 ? options.Metadata : metadata,
            Usage = BuildMaestroUsage(),
            Output = output,
            Error = string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? new ResponseResultError { Code = "maestro_failed", Message = GetMaestroFailureMessage(run) }
                : null
        };
    }

    private ChatCompletion ToMaestroChatCompletion(Ai21MaestroRun run, ChatCompletionOptions options)
    {
        var text = ExtractMaestroResultText(run);
        return new ChatCompletion
        {
            Id = run.Id,
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
            Usage = BuildMaestroUsage(),
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
                    message = new
                    {
                        role = "assistant",
                        content = text
                    }
                }
            ]
        };
    }

    private async Task<Ai21MaestroRun> ExecuteMaestroRunAsync(Ai21MaestroCreateRunRequest request, CancellationToken cancellationToken)
    {
        var created = await CreateMaestroRunAsync(request, cancellationToken);

        if (!string.Equals(created.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
            return created;

        return await WaitForMaestroRunCompletionAsync(created.Id, cancellationToken);
    }

    private async Task<ResponseResult> ExecuteMaestroResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var request = BuildMaestroRunRequest(options);
        var run = await ExecuteMaestroRunAsync(request, cancellationToken);
        return ToMaestroResponseResult(run, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteMaestroResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = BuildMaestroRunRequest(options);
        var run = await ExecuteMaestroRunAsync(request, cancellationToken);
        var response = ToMaestroResponseResult(run, options);
        var responseId = response.Id;
        var itemId = $"msg_{responseId}";
        var sequence = 1;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = CloneResponseResult(response, "in_progress")
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = CloneResponseResult(response, "in_progress")
        };

        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ResponseError
            {
                SequenceNumber = sequence++,
                Message = GetMaestroFailureMessage(run),
                Param = "run_id",
                Code = "maestro_failed"
            };

            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = response
            };

            yield break;
        }

        var text = ExtractMaestroResultText(run);
        foreach (var chunk in ChunkText(text))
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
            Text = text
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = response
        };
    }

    private async IAsyncEnumerable<UIMessagePart> ExecuteMaestroUiStreamAsync(
        ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = BuildMaestroRunRequest(chatRequest);
        var run = await ExecuteMaestroRunAsync(request, cancellationToken);
        var responseId = run.Id;
        var fullText = ExtractMaestroResultText(run);
        var textStarted = false;

        foreach (var source in run.DataSources?.WebSearch ?? [])
        {
            if (!string.IsNullOrWhiteSpace(source.Url))
                yield return ToSourcePart(source);
        }

        foreach (var document in run.DataSources?.FileSearch ?? [])
            yield return ToDocumentPart(document);

        foreach (var toolCall in run.DataSources?.ToolCalls ?? [])
        {
            yield return ToToolStartPart(toolCall);

            var delta = ToToolDeltaPart(toolCall);
            if (delta is not null)
                yield return delta;

            yield return ToToolOutputPart(toolCall);
        }

        if (!string.IsNullOrWhiteSpace(fullText))
        {
            yield return responseId.ToTextStartUIMessageStreamPart();
            textStarted = true;

            foreach (var chunk in ChunkText(fullText))
            {
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = responseId,
                    Delta = chunk
                };
            }
        }

        if (textStarted)
            yield return responseId.ToTextEndUIMessageStreamPart();

        var structured = ExtractMaestroStructuredResult(run) ?? TryParseStructuredOutput(fullText, chatRequest.ResponseFormat);
        if (structured is not null)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            yield return new DataUIPart
            {
                Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                Data = structured
            };
        }

        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
            yield return GetMaestroFailureMessage(run).ToErrorUIPart();

        yield return (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop")
            .ToFinishUIPart(
                chatRequest.Model,
                0,
                0,
                0,
                chatRequest.Temperature,
                extraMetadata: BuildMaestroFinishMetadata(run));
    }

    private async Task<ChatCompletion> ExecuteMaestroChatCompletionAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var request = BuildMaestroRunRequest(options);
        var run = await ExecuteMaestroRunAsync(request, cancellationToken);
        return ToMaestroChatCompletion(run, options);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> ExecuteMaestroChatCompletionStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = BuildMaestroRunRequest(options);
        var run = await ExecuteMaestroRunAsync(request, cancellationToken);
        var text = ExtractMaestroResultText(run);

        foreach (var chunk in ChunkText(text))
            yield return CreateMaestroChatCompletionChunk(run.Id, options.Model, chunk);

        yield return CreateMaestroChatCompletionChunk(
            run.Id,
            options.Model,
            null,
            string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
            BuildMaestroUsage());
    }

    private Ai21MaestroCreateRunRequest BuildMaestroRunRequest(ChatRequest chatRequest)
    {
        var metadata = ReadMaestroMetadata(chatRequest.ProviderMetadata);
        var maestroInput = ToMaestroInput(chatRequest.Messages, metadata.SystemPrompt);

        return new Ai21MaestroCreateRunRequest
        {
            Input = maestroInput.Input,
            SystemPrompt = maestroInput.SystemPrompt,
            Requirements = metadata.Requirements,
            Tools = metadata.Tools,
            Models = [NormalizeMaestroModel(chatRequest.Model)],
            Budget = metadata.Budget,
            Include = ResolveInclude(metadata.Include, includeDataSources: true, includeRequirementsResult: true),
            ResponseLanguage = metadata.ResponseLanguage
        };
    }

    private Ai21MaestroCreateRunRequest BuildMaestroRunRequest(ChatCompletionOptions options)
    {
        var maestroInput = ToMaestroInput(options.Messages, null);
        var nativeTools = NormalizeNativeMaestroTools(options.Tools);
        var requirements = BuildResponseFormatRequirements(options.ResponseFormat);

        return new Ai21MaestroCreateRunRequest
        {
            Input = maestroInput.Input,
            SystemPrompt = maestroInput.SystemPrompt,
            Requirements = requirements,
            Tools = nativeTools,
            Models = [NormalizeMaestroModel(options.Model)],
            Include = ResolveInclude(null, includeDataSources: true, includeRequirementsResult: requirements?.Count > 0)
        };
    }

    private Ai21MaestroCreateRunRequest BuildMaestroRunRequest(ResponseRequest options)
    {
        var metadata = ReadMaestroMetadata(options.Metadata);
        var maestroInput = ToMaestroInput(options.Input, options.Instructions, metadata.SystemPrompt);
        var requirements = CombineRequirements(metadata.Requirements, BuildResponseFormatRequirements(options.Text));
        var nativeTools = metadata.Tools ?? NormalizeNativeMaestroTools(options.Tools?.Cast<object>());

        return new Ai21MaestroCreateRunRequest
        {
            Input = maestroInput.Input,
            SystemPrompt = maestroInput.SystemPrompt,
            Requirements = requirements,
            Tools = nativeTools,
            Models = [NormalizeMaestroModel(options.Model)],
            Budget = metadata.Budget,
            Include = ResolveInclude(metadata.Include, includeDataSources: true, includeRequirementsResult: requirements?.Count > 0),
            ResponseLanguage = metadata.ResponseLanguage
        };
    }

    private static string NormalizeMaestroModel(string? model) => MaestroModelId;

    private static MaestroInputPayload ToMaestroInput(IEnumerable<UIMessage> messages, string? explicitSystemPrompt)
    {
        var conversational = new List<object>();
        var singleUser = new StringBuilder();
        string? systemPrompt = explicitSystemPrompt;

        foreach (var message in messages ?? [])
        {
            var text = ExtractText(message.Parts);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (message.Role == Role.system)
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            var role = message.Role == Role.assistant ? "assistant" : "user";
            conversational.Add(new { role, content = text });
            if (message.Role != Role.assistant)
                singleUser = AppendLine(singleUser, text);
        }

        object input = conversational.Count == 1 && singleUser.Length > 0
            ? singleUser.ToString()
            : conversational;

        return new MaestroInputPayload(systemPrompt, input);
    }

    private static MaestroInputPayload ToMaestroInput(IEnumerable<ChatMessage> messages, string? explicitSystemPrompt)
    {
        var conversational = new List<object>();
        var userOnly = new StringBuilder();
        var systemPrompt = explicitSystemPrompt;

        foreach (var message in messages ?? [])
        {
            var text = message.Content.ToAi21ContentString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase))
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
            conversational.Add(new { role, content = text });
            if (role == "user")
                userOnly = AppendLine(userOnly, text);
        }

        object input = conversational.Count == 1 && userOnly.Length > 0 ? userOnly.ToString() : conversational;
        return new MaestroInputPayload(systemPrompt, input);
    }

    private static MaestroInputPayload ToMaestroInput(ResponseInput? input, string? instructions, string? explicitSystemPrompt)
    {
        var systemPrompt = AppendParagraph(explicitSystemPrompt, instructions);

        if (input is null)
            return new MaestroInputPayload(systemPrompt, string.Empty);

        if (input.IsText)
            return new MaestroInputPayload(systemPrompt, input.Text ?? string.Empty);

        var conversational = new List<object>();
        var userOnly = new StringBuilder();

        foreach (var item in input.Items ?? [])
        {
            if (item is not ResponseInputMessage message)
                continue;

            var text = message.Content.Text ?? string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(a => a.Text) ?? []);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (message.Role is ResponseRole.System or ResponseRole.Developer)
            {
                systemPrompt = AppendParagraph(systemPrompt, text);
                continue;
            }

            var role = message.Role == ResponseRole.Assistant ? "assistant" : "user";
            conversational.Add(new { role, content = text });
            if (role == "user")
                userOnly = AppendLine(userOnly, text);
        }

        object payload = conversational.Count == 1 && userOnly.Length > 0 ? userOnly.ToString() : conversational;
        return new MaestroInputPayload(systemPrompt, payload);
    }

    private static string ExtractText(IEnumerable<UIMessagePart>? parts)
        => string.Join("\n", parts?.OfType<TextUIPart>().Select(a => a.Text).Where(a => !string.IsNullOrWhiteSpace(a)) ?? []);

    private static StringBuilder AppendLine(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
            builder.AppendLine();
        builder.Append(text);
        return builder;
    }

    private static string? AppendParagraph(string? existing, string? addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
            return existing;
        if (string.IsNullOrWhiteSpace(existing))
            return addition;
        return existing + "\n\n" + addition;
    }

    private static object? TryParseStructuredOutput(string text, object? responseFormat)
    {
        if (responseFormat is null || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(text, MaestroJson);
        }
        catch
        {
            return null;
        }
    }

    private static List<object>? BuildResponseFormatRequirements(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var schema = responseFormat.GetJSONSchema();
        if (schema?.JsonSchema is null)
            return null;

        return
        [
            new
            {
                name = schema.JsonSchema.Name ?? "structured_output",
                description = $"Return valid JSON matching this schema: {schema.JsonSchema.Schema.GetRawText()}",
                is_mandatory = schema.JsonSchema.Strict ?? true
            }
        ];
    }

    private static List<object>? NormalizeNativeMaestroTools(IEnumerable<object>? tools)
    {
        if (tools is null)
            return null;

        var list = new List<object>();
        foreach (var tool in tools)
        {
            var el = JsonSerializer.SerializeToElement(tool, MaestroJson);
            if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                continue;

            var type = typeEl.GetString();
            if (type is "mcp" or "http" or "file_search" or "web_search")
                list.Add(JsonSerializer.Deserialize<object>(el.GetRawText(), MaestroJson)!);
        }

        return list.Count == 0 ? null : list;
    }

    private static List<object>? CombineRequirements(List<object>? first, List<object>? second)
    {
        if (first is null || first.Count == 0)
            return second;
        if (second is null || second.Count == 0)
            return first;
        return [.. first, .. second];
    }

    private static List<string> ResolveInclude(List<string>? include, bool includeDataSources, bool includeRequirementsResult)
    {
        var values = new HashSet<string>(include ?? [], StringComparer.OrdinalIgnoreCase);
        if (includeDataSources)
            values.Add("data_sources");
        if (includeRequirementsResult)
            values.Add("requirements_result");
        return [.. values];
    }

    private static Dictionary<string, object> BuildMaestroFinishMetadata(Ai21MaestroRun run)
    {
        var metadata = new Dictionary<string, object>
        {
            ["runId"] = run.Id,
            ["status"] = run.Status
        };

        if (run.RequirementsResult?.Score is not null)
            metadata["requirementsScore"] = run.RequirementsResult.Score.Value;
        if (!string.IsNullOrWhiteSpace(run.RequirementsResult?.FinishReason))
            metadata["requirementsFinishReason"] = run.RequirementsResult!.FinishReason!;
        if (run.DataSources?.WebSearch?.Count > 0)
            metadata["webSearchCount"] = run.DataSources.WebSearch.Count;
        if (run.DataSources?.FileSearch?.Count > 0)
            metadata["fileSearchCount"] = run.DataSources.FileSearch.Count;
        if (run.DataSources?.ToolCalls?.Count > 0)
            metadata["toolCallCount"] = run.DataSources.ToolCalls.Count;

        return metadata;
    }

    private static Ai21MaestroMetadata ReadMaestroMetadata(Dictionary<string, JsonElement>? providerMetadata)
    {
        if (providerMetadata is null)
            return new Ai21MaestroMetadata();

        if (providerMetadata.TryGetValue("ai21", out var direct) && direct.ValueKind == JsonValueKind.Object)
            return direct.Deserialize<Ai21MaestroMetadata>(MaestroJson) ?? new Ai21MaestroMetadata();

        return new Ai21MaestroMetadata();
    }

    private static Ai21MaestroMetadata ReadMaestroMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return new Ai21MaestroMetadata();

        if (metadata.TryGetValue("ai21", out var raw) && raw is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<Ai21MaestroMetadata>(JsonSerializer.Serialize(raw, MaestroJson), MaestroJson)
                    ?? new Ai21MaestroMetadata();
            }
            catch
            {
                return new Ai21MaestroMetadata();
            }
        }

        return new Ai21MaestroMetadata();
    }

    private sealed class Ai21MaestroMetadata
    {
        [JsonPropertyName("system_prompt")]
        public string? SystemPrompt { get; init; }

        [JsonPropertyName("requirements")]
        public List<object>? Requirements { get; init; }

        [JsonPropertyName("tools")]
        public List<object>? Tools { get; init; }

        [JsonPropertyName("include")]
        public List<string>? Include { get; init; }

        [JsonPropertyName("budget")]
        public string? Budget { get; init; }

        [JsonPropertyName("response_language")]
        public string? ResponseLanguage { get; init; }
    }

    private sealed class MaestroInputPayload(string? systemPrompt, object input)
    {
        public string? SystemPrompt { get; } = systemPrompt;
        public object Input { get; } = input;
    }

    private static ResponseResult CloneResponseResult(ResponseResult source, string status)
        => new()
        {
            Id = source.Id,
            Object = source.Object,
            CreatedAt = source.CreatedAt,
            CompletedAt = source.CompletedAt,
            Status = status,
            ParallelToolCalls = source.ParallelToolCalls,
            Model = source.Model,
            Temperature = source.Temperature,
            Output = source.Output,
            Usage = source.Usage,
            Text = source.Text,
            ToolChoice = source.ToolChoice,
            Tools = source.Tools,
            Reasoning = source.Reasoning,
            Store = source.Store,
            MaxOutputTokens = source.MaxOutputTokens,
            Error = source.Error,
            Metadata = source.Metadata
        };

    private static ChatCompletionUpdate CreateMaestroChatCompletionChunk(
        string id,
        string model,
        string? delta,
        string? finishReason = null,
        object? usage = null)
        => new()
        {
            Id = id,
            Object = "chat.completion.chunk",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Usage = usage,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = delta is not null ? "assistant" : (string?)null,
                        content = delta
                    },
                    finish_reason = finishReason
                }
            ]
        };

    private sealed class Ai21MaestroCreateRunRequest
    {
        [JsonPropertyName("input")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Input { get; init; }

        [JsonPropertyName("system_prompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SystemPrompt { get; init; }

        [JsonPropertyName("requirements")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<object>? Requirements { get; init; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<object>? Tools { get; init; }

        [JsonPropertyName("models")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Models { get; init; }

        [JsonPropertyName("budget")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Budget { get; init; }

        [JsonPropertyName("include")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Include { get; init; }

        [JsonPropertyName("response_language")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseLanguage { get; init; }
    }

    private sealed class Ai21MaestroRun
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = Guid.NewGuid().ToString("n");

        [JsonPropertyName("status")]
        public string Status { get; init; } = "in_progress";

        [JsonPropertyName("result")]
        public JsonElement? Result { get; init; }

        [JsonPropertyName("data_sources")]
        public Ai21MaestroDataSources? DataSources { get; init; }

        [JsonPropertyName("requirements_result")]
        public Ai21MaestroRequirementsResult? RequirementsResult { get; init; }

        [JsonPropertyName("error")]
        public Ai21MaestroError? Error { get; init; }
    }

    private sealed class Ai21MaestroDataSources
    {
        [JsonPropertyName("web_search")]
        public List<Ai21MaestroWebSearchSource>? WebSearch { get; init; }

        [JsonPropertyName("file_search")]
        public List<Ai21MaestroFileSearchSource>? FileSearch { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<Ai21MaestroToolCall>? ToolCalls { get; init; }
    }

    private sealed class Ai21MaestroWebSearchSource
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }
    }

    private sealed class Ai21MaestroFileSearchSource
    {
        [JsonPropertyName("file_id")]
        public string? FileId { get; init; }

        [JsonPropertyName("file_name")]
        public string? FileName { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }

        [JsonPropertyName("order")]
        public int? Order { get; init; }
    }

    private sealed class Ai21MaestroToolCall
    {
        [JsonPropertyName("tool_name")]
        public string? ToolName { get; init; }

        [JsonPropertyName("tool_type")]
        public string? ToolType { get; init; }

        [JsonPropertyName("server_label")]
        public string? ServerLabel { get; init; }

        [JsonPropertyName("parameters")]
        public JsonElement? Parameters { get; init; }

        [JsonPropertyName("response")]
        public JsonElement? Response { get; init; }
    }

    private sealed class Ai21MaestroRequirementsResult
    {
        [JsonPropertyName("score")]
        public double? Score { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }

        [JsonPropertyName("requirements")]
        public List<Ai21MaestroRequirement>? Requirements { get; init; }
    }

    private sealed class Ai21MaestroRequirement
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("score")]
        public double? Score { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("is_mandatory")]
        public bool? IsMandatory { get; init; }
    }

    private sealed class Ai21MaestroError
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
