using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    private const string ResearchPath = "v1/research";
    private const string AgentsRunsPath = "v1/agents/runs";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private void ApplyResearchAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(YouCom)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Add("X-API-Key", key);
    }

    private void ApplyAgentAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(YouCom)} API key.");

        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static bool IsResearchModel(string? model)
        => NormalizeModelName(model) is "lite" or "standard" or "deep" or "exhaustive";

    private static bool IsAgentModel(string? model)
        => NormalizeModelName(model) is "express" or "advanced";

    private static bool IsExpressModel(string? model)
        => NormalizeModelName(model) == "express";

    private static bool IsAdvancedModel(string? model)
        => NormalizeModelName(model) == "advanced";

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var idx = model.IndexOf('/');
        return (idx >= 0 ? model[(idx + 1)..] : model).Trim().ToLowerInvariant();
    }

    private static string ResolveResearchEffort(string? model, string? overrideValue = null)
    {
        var normalizedOverride = NormalizeResearchEffort(overrideValue);
        if (!string.IsNullOrWhiteSpace(normalizedOverride))
            return normalizedOverride!;

        return NormalizeModelName(model) switch
        {
            "lite" => "lite",
            "deep" => "deep",
            "exhaustive" => "exhaustive",
            _ => "standard"
        };
    }

    private static string? NormalizeResearchEffort(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "lite" => "lite",
            "standard" => "standard",
            "deep" => "deep",
            "exhaustive" => "exhaustive",
            _ => null
        };

    private static string? NormalizeAdvancedSearchEffort(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null
        };

    private static string? NormalizeAdvancedVerbosity(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => null
        };

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var message in all)
        {
            var text = string.Concat(message.Parts.OfType<TextUIPart>().Select(part => part.Text));
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add($"system: {request.Instructions}");

        if (request.Input?.IsText == true && !string.IsNullOrWhiteSpace(request.Input.Text))
            lines.Add($"user: {request.Input.Text}");

        if (request.Input?.IsItems == true)
        {
            foreach (var item in request.Input.Items ?? [])
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var text = ExtractResponseMessageText(message.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                lines.Add($"{message.Role.ToString().ToLowerInvariant()}: {text}");
            }
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromSamplingMessages(IEnumerable<SamplingMessage>? messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var msg in all)
        {
            var role = msg.Role switch
            {
                ModelContextProtocol.Protocol.Role.Assistant => "assistant",
                ModelContextProtocol.Protocol.Role.User => "user",
                _ => "user"
            };

            var text = msg.ToText() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var message in all)
        {
            var text = message.Content.GetRawText();
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string ExtractResponseMessageText(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text ?? string.Empty;

        var builder = new StringBuilder();
        foreach (var part in content.Parts ?? [])
        {
            if (part is InputTextPart text && !string.IsNullOrWhiteSpace(text.Text))
                builder.Append(text.Text);
        }

        return builder.ToString();
    }

    private static string ApplyStructuredOutputInstructions(string prompt, object? responseFormat)
    {
        var schemaText = ExtractSchemaText(responseFormat);
        if (string.IsNullOrWhiteSpace(schemaText))
            return prompt;

        return string.IsNullOrWhiteSpace(prompt)
            ? $"Return JSON that matches this schema exactly:\n{schemaText}"
            : $"{prompt}\n\nReturn JSON that matches this schema exactly:\n{schemaText}";
    }

    private static string? ExtractSchemaText(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var schema = responseFormat.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(responseFormat, Json);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryParseStructuredOutput(string text, object? responseFormat)
    {
        if (responseFormat is null || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(text, Json);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize = 180)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }

    private static SourceUIPart ToSourcePart(YouComSourceInfo source)
    {
        Dictionary<string, object>? providerMetadata = null;

        if ((source.Snippets?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(source.SourceType) || !string.IsNullOrWhiteSpace(source.CitationUri))
        {
            providerMetadata = [];

            if ((source.Snippets?.Count ?? 0) > 0)
                providerMetadata["snippets"] = source.Snippets!.ToArray();

            if (!string.IsNullOrWhiteSpace(source.SourceType))
                providerMetadata["sourceType"] = source.SourceType!;

            if (!string.IsNullOrWhiteSpace(source.CitationUri))
                providerMetadata["citationUri"] = source.CitationUri!;
        }

        return new SourceUIPart
        {
            SourceId = source.Url,
            Url = source.Url,
            Title = string.IsNullOrWhiteSpace(source.Title) ? null : source.Title,
            ProviderMetadata = providerMetadata?.ToProviderMetadata("youcom")
        };
    }

    private static Dictionary<string, object?> MergeResponseMetadata(
        Dictionary<string, object?>? current,
        string endpoint,
        IEnumerable<YouComSourceInfo> sources,
        long? runtimeMs = null)
    {
        var merged = current is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(current, StringComparer.OrdinalIgnoreCase);

        merged["youcom_endpoint"] = endpoint;

        if (runtimeMs is not null)
            merged["youcom_runtime_ms"] = runtimeMs.Value;

        var sourceArray = sources.Select(source => new Dictionary<string, object?>
        {
            ["url"] = source.Url,
            ["title"] = source.Title,
            ["snippets"] = source.Snippets?.ToArray(),
            ["sourceType"] = source.SourceType,
            ["citationUri"] = source.CitationUri
        }).Cast<object>().ToArray();

        if (sourceArray.Length > 0)
            merged["sources"] = sourceArray;

        return merged;
    }

    private static JsonObject BuildSamplingMeta(YouComExecutionResult result)
    {
        var meta = new JsonObject
        {
            ["inputTokens"] = 0,
            ["outputTokens"] = 0,
            ["totalTokens"] = 0,
            ["youcomEndpoint"] = result.Endpoint
        };

        if (result.RuntimeMs is not null)
            meta["runtimeMs"] = result.RuntimeMs.Value;

        if (result.Sources.Count > 0)
        {
            meta["sources"] = JsonSerializer.SerializeToNode(
                result.Sources.Select(source => new
                {
                    url = source.Url,
                    title = source.Title,
                    snippets = source.Snippets,
                    sourceType = source.SourceType,
                    citationUri = source.CitationUri
                }),
                Json);
        }

        return meta;
    }

    private static object BuildChatUsage()
        => new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 };

    private static object BuildResponseUsage()
        => new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 };

    private static object[] BuildResponseOutput(YouComExecutionResult result)
    {
        object contentPart = result.Sources.Count > 0
            ? new
            {
                type = "output_text",
                text = result.Text,
                annotations = result.Sources.Select((source, index) => new
                {
                    type = "url_citation",
                    title = source.Title ?? source.Url,
                    url = source.Url,
                    source_id = source.Url,
                    index = index + 1
                }).ToArray()
            }
            : new
            {
                type = "output_text",
                text = result.Text
            };

        return
        [
            new
            {
                id = $"msg_{result.Id}",
                type = "message",
                role = "assistant",
                content = new[] { contentPart }
            }
        ];
    }

    private static string ExtractOutputText(IEnumerable<object>? output)
    {
        if (output is null)
            return string.Empty;

        foreach (var item in output)
        {
            var json = JsonSerializer.SerializeToElement(item, Json);
            if (!json.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in contentEl.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && typeEl.GetString() == "output_text"
                    && part.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    return textEl.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static YouComRequestMetadata ReadResponseMetadata(ResponseRequest request)
    {
        if (request.Metadata is null)
            return new YouComRequestMetadata();

        if (request.Metadata.TryGetValue("youcom", out var nested) && TryDeserializeMetadata(nested) is { } metadata)
            return metadata;

        return new YouComRequestMetadata
        {
            ResearchEffort = request.Metadata.TryGetValue("youcom:research_effort", out var researchEffort)
                ? researchEffort?.ToString()
                : null,
            Verbosity = request.Metadata.TryGetValue("youcom:verbosity", out var verbosity)
                ? verbosity?.ToString()
                : null,
            SearchEffort = request.Metadata.TryGetValue("youcom:search_effort", out var searchEffort)
                ? searchEffort?.ToString()
                : null,
            ReportVerbosity = request.Metadata.TryGetValue("youcom:report_verbosity", out var reportVerbosity)
                ? reportVerbosity?.ToString()
                : null,
            WebSearch = request.Metadata.TryGetValue("youcom:web_search", out var webSearch) && TryGetBoolean(webSearch),
            UseResearchTool = request.Metadata.TryGetValue("youcom:use_research_tool", out var useResearch) && TryGetBoolean(useResearch),
            UseComputeTool = request.Metadata.TryGetValue("youcom:use_compute_tool", out var useCompute) && TryGetBoolean(useCompute),
            MaxWorkflowSteps = request.Metadata.TryGetValue("youcom:max_workflow_steps", out var maxSteps)
                ? TryGetInteger(maxSteps)
                : null
        };
    }

    private static YouComRequestMetadata ReadChatMetadata(ChatRequest request)
        => request.GetProviderMetadata<YouComRequestMetadata>(nameof(YouCom).ToLowerInvariant()) ?? new YouComRequestMetadata();

    private static YouComRequestMetadata? TryDeserializeMetadata(object? raw)
    {
        if (raw is null)
            return null;

        try
        {
            return raw switch
            {
                JsonElement element => element.Deserialize<YouComRequestMetadata>(Json),
                JsonNode node => node.Deserialize<YouComRequestMetadata>(Json),
                _ => JsonSerializer.Deserialize<YouComRequestMetadata>(JsonSerializer.Serialize(raw, Json), Json)
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBoolean(object? raw)
    {
        return raw switch
        {
            bool value => value,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            _ when bool.TryParse(raw?.ToString(), out var parsed) => parsed,
            _ => false
        };
    }

    private static int? TryGetInteger(object? raw)
    {
        return raw switch
        {
            int value => value,
            long value => (int)value,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue) => intValue,
            _ when int.TryParse(raw?.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static List<object>? BuildAgentTools(string model, bool toolHint, YouComRequestMetadata metadata)
    {
        if (IsExpressModel(model))
        {
            if (metadata.WebSearch == true || toolHint)
            {
                return [new Dictionary<string, object> { ["type"] = "web_search" }];
            }

            return null;
        }

        if (!IsAdvancedModel(model))
            return null;

        var tools = new List<object>();

        if (metadata.UseComputeTool == true)
            tools.Add(new Dictionary<string, object> { ["type"] = "compute" });

        if (metadata.UseResearchTool == true || toolHint)
        {
            tools.Add(new Dictionary<string, object?>
            {
                ["type"] = "research",
                ["search_effort"] = NormalizeAdvancedSearchEffort(metadata.SearchEffort) ?? "low",
                ["report_verbosity"] = NormalizeAdvancedVerbosity(metadata.ReportVerbosity) ?? "medium"
            });
        }

        return tools.Count == 0 ? null : tools;
    }

    private async Task<YouComExecutionResult> ExecuteResearchAsync(
        string requestedModel,
        string prompt,
        object? responseFormat,
        string? researchEffortOverride,
        CancellationToken cancellationToken)
    {
        ApplyResearchAuthHeader();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new YouComResearchRequest
        {
            Input = ApplyStructuredOutputInstructions(prompt, responseFormat),
            ResearchEffort = ResolveResearchEffort(requestedModel, researchEffortOverride)
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ResearchPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"You.com research error ({(int)resp.StatusCode}): {(string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<YouComResearchResponse>(stream, Json, cancellationToken)
                     ?? throw new InvalidOperationException("You.com research returned an empty response.");

        return new YouComExecutionResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = requestedModel,
            Endpoint = "research",
            Text = result.Output?.Content ?? string.Empty,
            Sources = result.Output?.Sources?
                .Where(source => !string.IsNullOrWhiteSpace(source.Url))
                .GroupBy(source => source.Url!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new YouComSourceInfo(
                    group.Key,
                    group.First().Title,
                    group.SelectMany(source => source.Snippets ?? []).Where(snippet => !string.IsNullOrWhiteSpace(snippet)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    "research",
                    group.Key))
                .ToList() ?? [],
            FinishReason = "stop",
            CreatedAt = now,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private async IAsyncEnumerable<YouComAgentStreamEvent> StreamAgentEventsAsync(
        string requestedModel,
        string prompt,
        object? responseFormat,
        bool toolHint,
        YouComRequestMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAgentAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["agent"] = NormalizeModelName(requestedModel),
            ["input"] = ApplyStructuredOutputInstructions(prompt, responseFormat),
            ["stream"] = true
        };

        var tools = BuildAgentTools(requestedModel, toolHint, metadata);
        if (tools is not null)
            payload["tools"] = tools;

        if (IsAdvancedModel(requestedModel))
        {
            var verbosity = NormalizeAdvancedVerbosity(metadata.Verbosity);
            if (!string.IsNullOrWhiteSpace(verbosity))
                payload["verbosity"] = verbosity;

            if (metadata.MaxWorkflowSteps is int maxWorkflowSteps && maxWorkflowSteps > 0)
            {
                payload["workflow_config"] = new Dictionary<string, object>
                {
                    ["max_workflow_steps"] = maxWorkflowSteps
                };
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, AgentsRunsPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        await foreach (var data in ReadSseDataAsync(req, cancellationToken))
        {
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                yield break;

            var evt = ParseAgentEvent(data);
            if (evt is not null)
                yield return evt;
        }
    }

    private async IAsyncEnumerable<string> ReadSseDataAsync(
        HttpRequestMessage request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"You.com agent error ({(int)response.StatusCode}): {(string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var dataLines = new List<string>();
         string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (line is null)
                break;

            if (line.StartsWith(':'))
                continue;

            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join("\n", dataLines);
                    dataLines.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line[5..].TrimStart());
        }

        if (dataLines.Count > 0)
            yield return string.Join("\n", dataLines);
    }

    private static YouComAgentStreamEvent? ParseAgentEvent(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;

            var type = typeEl.GetString() ?? string.Empty;
            var evt = new YouComAgentStreamEvent
            {
                Type = type,
                SequenceId = root.TryGetProperty("seq_id", out var seqEl) && seqEl.TryGetInt32(out var seq) ? seq : 0
            };

            if (!root.TryGetProperty("response", out var responseEl) || responseEl.ValueKind != JsonValueKind.Object)
                return evt;

            switch (type)
            {
                case "response.output_text.delta":
                    evt.OutputIndex = responseEl.TryGetProperty("output_index", out var outputIndexEl) && outputIndexEl.TryGetInt32(out var outputIndex)
                        ? outputIndex
                        : 0;
                    evt.Delta = responseEl.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                        ? deltaEl.GetString()
                        : null;
                    break;

                case "response.output_content.full":
                    evt.OutputIndex = responseEl.TryGetProperty("output_index", out var fullOutputIndexEl) && fullOutputIndexEl.TryGetInt32(out var fullOutputIndex)
                        ? fullOutputIndex
                        : 0;

                    if (responseEl.TryGetProperty("full", out var fullEl) && fullEl.ValueKind == JsonValueKind.Array)
                    {
                        evt.Sources = fullEl.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.Object
                                           && item.TryGetProperty("url", out var urlEl)
                                           && urlEl.ValueKind == JsonValueKind.String
                                           && !string.IsNullOrWhiteSpace(urlEl.GetString()))
                            .Select(item => new YouComSourceInfo(
                                item.GetProperty("url").GetString()!,
                                item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String ? titleEl.GetString() : null,
                                item.TryGetProperty("snippet", out var snippetEl) && snippetEl.ValueKind == JsonValueKind.String
                                    ? new List<string> { snippetEl.GetString()! }
                                    : [],
                                item.TryGetProperty("source_type", out var sourceTypeEl) && sourceTypeEl.ValueKind == JsonValueKind.String ? sourceTypeEl.GetString() : null,
                                item.TryGetProperty("citation_uri", out var citationUriEl) && citationUriEl.ValueKind == JsonValueKind.String ? citationUriEl.GetString() : null))
                            .ToList();
                    }
                    break;

                case "response.done":
                    evt.RuntimeMs = responseEl.TryGetProperty("run_time_ms", out var runtimeEl)
                        ? runtimeEl.ValueKind switch
                        {
                            JsonValueKind.String when long.TryParse(runtimeEl.GetString(), out var parsed) => parsed,
                            JsonValueKind.Number when runtimeEl.TryGetInt64(out var numeric) => numeric,
                            _ => null
                        }
                        : null;
                    evt.Finished = responseEl.TryGetProperty("finished", out var finishedEl) && finishedEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? finishedEl.GetBoolean()
                        : null;
                    break;
            }

            return evt;
        }
        catch
        {
            return null;
        }
    }

    private static ChatCompletion ToChatCompletion(YouComExecutionResult result)
    {
        return new ChatCompletion
        {
            Id = result.Id,
            Object = "chat.completion",
            Created = result.CreatedAt,
            Model = result.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = result.Text
                    },
                    finish_reason = result.FinishReason
                }
            ],
            Usage = BuildChatUsage()
        };
    }

    private static ResponseResult ToResponseResult(YouComExecutionResult result, ResponseRequest request)
    {
        return new ResponseResult
        {
            Id = result.Id,
            Object = "response",
            CreatedAt = result.CreatedAt,
            CompletedAt = result.CompletedAt,
            Status = result.FinishReason == "stop" ? "completed" : "failed",
            Model = result.Model,
            Temperature = request.Temperature,
            Metadata = MergeResponseMetadata(request.Metadata, result.Endpoint, result.Sources, result.RuntimeMs),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = BuildResponseUsage(),
            Error = result.FinishReason == "stop"
                ? null
                : new ResponseResultError
                {
                    Code = "youcom_agent_failed",
                    Message = string.IsNullOrWhiteSpace(result.Error) ? "You.com agent run did not finish successfully." : result.Error
                },
            Output = BuildResponseOutput(result)
        };
    }

    private static CreateMessageResult ToSamplingResult(YouComExecutionResult result)
    {
        return new CreateMessageResult
        {
            Model = result.Model,
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            StopReason = result.FinishReason,
            Content = [result.Text.ToTextContentBlock()],
            Meta = BuildSamplingMeta(result)
        };
    }

    private sealed class YouComExecutionResult
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("n");

        public string Model { get; init; } = default!;

        public string Endpoint { get; init; } = default!;

        public string Text { get; init; } = string.Empty;

        public List<YouComSourceInfo> Sources { get; init; } = [];

        public string FinishReason { get; init; } = "stop";

        public long CreatedAt { get; init; }

        public long CompletedAt { get; init; }

        public long? RuntimeMs { get; init; }

        public string? Error { get; init; }
    }

    private sealed record YouComSourceInfo(
        string Url,
        string? Title,
        IReadOnlyList<string>? Snippets,
        string? SourceType,
        string? CitationUri);

    private sealed class YouComAgentStreamEvent
    {
        public string Type { get; init; } = default!;

        public int SequenceId { get; set; }

        public int OutputIndex { get; set; }

        public string? Delta { get; set; }

        public List<YouComSourceInfo>? Sources { get; set; }

        public long? RuntimeMs { get; set; }

        public bool? Finished { get; set; }
    }

    private sealed class YouComRequestMetadata
    {
        [JsonPropertyName("researchEffort")]
        public string? ResearchEffort { get; init; }

        [JsonPropertyName("verbosity")]
        public string? Verbosity { get; init; }

        [JsonPropertyName("searchEffort")]
        public string? SearchEffort { get; init; }

        [JsonPropertyName("reportVerbosity")]
        public string? ReportVerbosity { get; init; }

        [JsonPropertyName("webSearch")]
        public bool? WebSearch { get; init; }

        [JsonPropertyName("useResearchTool")]
        public bool? UseResearchTool { get; init; }

        [JsonPropertyName("useComputeTool")]
        public bool? UseComputeTool { get; init; }

        [JsonPropertyName("maxWorkflowSteps")]
        public int? MaxWorkflowSteps { get; init; }
    }

    private sealed class YouComResearchRequest
    {
        [JsonPropertyName("input")]
        public string Input { get; init; } = string.Empty;

        [JsonPropertyName("research_effort")]
        public string? ResearchEffort { get; init; }
    }

    private sealed class YouComResearchResponse
    {
        [JsonPropertyName("output")]
        public YouComResearchOutput? Output { get; init; }
    }

    private sealed class YouComResearchOutput
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("content_type")]
        public string? ContentType { get; init; }

        [JsonPropertyName("sources")]
        public List<YouComResearchSource>? Sources { get; init; }
    }

    private sealed class YouComResearchSource
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("snippets")]
        public List<string>? Snippets { get; init; }
    }
}
