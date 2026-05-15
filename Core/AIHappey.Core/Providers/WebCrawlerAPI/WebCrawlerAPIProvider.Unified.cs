using System.Globalization;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.WebCrawlerAPI;

public partial class WebCrawlerAPIProvider
{
    private static readonly JsonSerializerOptions WebCrawlerAPIJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(10);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerMetadata = GetWebCrawlerAPIProviderMetadata(request);
        var payload = BuildAgentPayload(request, providerMetadata);
        var queued = await QueueAgentRunAsync(payload, cancellationToken);
        var completed = await WaitForAgentRunTerminalAsync(queued.Id, providerMetadata, cancellationToken);

        return ToUnifiedResponse(completed, request, payload);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var providerMetadata = GetWebCrawlerAPIProviderMetadata(request);
        var payload = BuildAgentPayload(request, providerMetadata);
        var queued = await QueueAgentRunAsync(payload, cancellationToken);
        var eventId = request.Id ?? queued.Id;

        yield return CreateStreamEvent(
            providerId,
            "data-webcrawlerapi-agent-run",
            queued.Id,
            new AIDataEventData
            {
                Id = queued.Id,
                Data = ToTaskData(queued),
                Transient = true
            },
            ParseDateTimeOffsetOrNow(queued.UpdatedAt ?? queued.CreatedAt),
            BuildQueuedRunMetadata(queued, payload));

        WebCrawlerAPIAgentRun? terminal = null;
        Exception? pollingException = null;

        await foreach (var polled in PollAgentRunUntilTerminalAsync(queued, providerMetadata, cancellationToken))
        {
            yield return CreateStreamEvent(
                providerId,
                "data-webcrawlerapi-agent-run",
                polled.Id,
                new AIDataEventData
                {
                    Id = polled.Id,
                    Data = ToTaskData(polled),
                    Transient = !IsTerminalStatus(polled.Status)
                },
                ParseDateTimeOffsetOrNow(polled.UpdatedAt ?? polled.CreatedAt),
                BuildQueuedRunMetadata(polled, payload));

            if (IsTerminalStatus(polled.Status))
            {
                terminal = polled;
                break;
            }
        }

        if (terminal is null)
        {
            try
            {
                terminal = await WaitForAgentRunTerminalAsync(queued.Id, providerMetadata, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pollingException = ex;
            }
        }

        if (pollingException is not null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            yield return CreateStreamEvent(
                providerId,
                "error",
                queued.Id,
                new AIErrorEventData { ErrorText = pollingException.Message },
                timestamp,
                BuildQueuedRunMetadata(queued, payload));

            yield return CreateFinishStreamEvent(providerId, eventId, request, null, payload, "error", timestamp);
            yield break;
        }

        if (terminal is null)
            throw new InvalidOperationException($"WebCrawlerAPI agent run '{queued.Id}' did not produce a terminal result.");

        var finalTimestamp = ParseDateTimeOffsetOrNow(terminal.UpdatedAt ?? terminal.CreatedAt);
        var metadata = BuildUnifiedResponseMetadata(request, terminal, payload);

        if (string.Equals(terminal.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var message = GetAgentRunErrorMessage(terminal);
            yield return CreateStreamEvent(
                providerId,
                "error",
                terminal.Id,
                new AIErrorEventData { ErrorText = message },
                finalTimestamp,
                metadata);

            yield return CreateFinishStreamEvent(providerId, eventId, request, terminal, payload, "error", finalTimestamp);
            yield break;
        }

        var outputText = ExtractAgentOutputText(terminal);
        if (!string.IsNullOrWhiteSpace(outputText))
        {
            yield return CreateStreamEvent(providerId, "text-start", eventId, new AITextStartEventData(), finalTimestamp, metadata);
            yield return CreateStreamEvent(providerId, "text-delta", eventId, new AITextDeltaEventData { Delta = outputText }, finalTimestamp, metadata);
            yield return CreateStreamEvent(providerId, "text-end", eventId, new AITextEndEventData(), finalTimestamp, metadata);
        }

        if (terminal.Data is JsonElement dataElement
            && dataElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            yield return CreateStreamEvent(
                providerId,
                "data-webcrawlerapi.output",
                eventId,
                new AIDataEventData
                {
                    Id = eventId,
                    Data = ToPlainObject(dataElement) ?? new { }
                },
                finalTimestamp,
                metadata);
        }

        foreach (var source in CreateSourceStreamEvents(providerId, terminal, finalTimestamp, metadata))
            yield return source;

        yield return CreateFinishStreamEvent(providerId, eventId, request, terminal, payload, ResolveFinishReason(terminal.Status), finalTimestamp);
    }

    private async Task<WebCrawlerAPIAgentRun> QueueAgentRunAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, WebCrawlerAPIJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/agent")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebCrawlerAPI agent run queue failed ({(int)resp.StatusCode}): {body}");

        return ParseAgentRun(body);
    }

    private async Task<WebCrawlerAPIAgentRun> GetAgentRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/agent/job/{Uri.EscapeDataString(runId)}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebCrawlerAPI agent run poll failed ({(int)resp.StatusCode}): {body}");

        return ParseAgentRun(body);
    }

    private async Task<WebCrawlerAPIAgentRun> WaitForAgentRunTerminalAsync(
        string runId,
        WebCrawlerAPIProviderMetadata metadata,
        CancellationToken cancellationToken)
        => await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetAgentRunAsync(runId, ct),
            isTerminal: run => IsTerminalStatus(run.Status),
            interval: ResolvePollInterval(metadata),
            timeout: ResolvePollTimeout(metadata),
            maxAttempts: metadata.PollMaxAttempts,
            cancellationToken: cancellationToken);

    private async IAsyncEnumerable<WebCrawlerAPIAgentRun> PollAgentRunUntilTerminalAsync(
        WebCrawlerAPIAgentRun queued,
        WebCrawlerAPIProviderMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var interval = ResolvePollInterval(metadata);
        var timeout = ResolvePollTimeout(metadata);
        var maxAttempts = metadata.PollMaxAttempts;
        var started = DateTimeOffset.UtcNow;
        var attempts = 0;
        var lastStatus = queued.Status;
        var lastUpdatedAt = queued.UpdatedAt;

        while (!IsTerminalStatus(lastStatus))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken);

            attempts++;
            var current = await GetAgentRunAsync(queued.Id, cancellationToken);
            var changed = !string.Equals(current.Status, lastStatus, StringComparison.OrdinalIgnoreCase)
                          || !string.Equals(current.UpdatedAt, lastUpdatedAt, StringComparison.Ordinal);

            lastStatus = current.Status;
            lastUpdatedAt = current.UpdatedAt;

            if (changed || IsTerminalStatus(current.Status))
                yield return current;

            if (IsTerminalStatus(current.Status))
                yield break;

            if (maxAttempts.HasValue && attempts >= maxAttempts.Value)
                throw new TimeoutException($"WebCrawlerAPI agent polling exceeded max attempts ({maxAttempts}). RunId={queued.Id}");

            if (timeout.HasValue && DateTimeOffset.UtcNow - started >= timeout.Value)
                throw new TimeoutException($"WebCrawlerAPI agent polling exceeded timeout ({timeout}). RunId={queued.Id}");
        }
    }

    private Dictionary<string, object?> BuildAgentPayload(
        AIRequest request,
        WebCrawlerAPIProviderMetadata metadata)
    {
        var prompt = BuildAgentPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("WebCrawlerAPI requires a non-empty prompt derived from unified input or instructions.");

        if (metadata.MaxSpendUsd is null || metadata.MaxSpendUsd <= 0)
            throw new InvalidOperationException("WebCrawlerAPI requires provider metadata 'maxSpendUsd' with a value greater than 0.");

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["max_spend_usd"] = metadata.MaxSpendUsd.Value
        };

        var urls = ResolveSeedUrls(request, metadata).ToList();
        if (urls.Count > 0)
            payload["urls"] = urls;

        if (metadata.SeedUrlsOnly is not null)
            payload["seed_urls_only"] = metadata.SeedUrlsOnly;

        var model = ResolveNativeModel(request.Model);
        if (!string.IsNullOrWhiteSpace(model))
            payload["model"] = model;

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        if (outputSchema is not null)
            payload["output_schema"] = outputSchema;

        if (metadata.AdditionalProperties is not null)
        {
            foreach (var property in metadata.AdditionalProperties)
            {
                if (IsReservedPayloadProperty(property.Key))
                    continue;

                payload[property.Key] = ToPlainObject(property.Value) ?? property.Value.Clone();
            }
        }

        return payload;
    }

    private WebCrawlerAPIProviderMetadata GetWebCrawlerAPIProviderMetadata(AIRequest request)
        => request.Metadata.GetProviderMetadata<WebCrawlerAPIProviderMetadata>(GetIdentifier())
           ?? throw new InvalidOperationException("WebCrawlerAPI requires provider metadata with 'maxSpendUsd'.");

    private static string BuildAgentPrompt(AIRequest request)
    {
        var instructionSections = new List<string>();
        var conversationSections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructionSections.Add(request.Instructions.Trim());

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            conversationSections.Add(FormatConversationBlock("user", request.Input.Text!));

        if (request.Input?.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Type)
                    && !string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = ExtractSupportedText(item.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var role = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role!.Trim().ToLowerInvariant();
                if (role == "system")
                    instructionSections.Add(text);
                else
                    conversationSections.Add(FormatConversationBlock(role, text));
            }
        }

        var sections = new List<string>();
        if (instructionSections.Count > 0)
            sections.Add("instructions:\n" + string.Join("\n\n", instructionSections));

        sections.AddRange(conversationSections);
        return string.Join("\n\n", sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    private static string FormatConversationBlock(string role, string text)
        => $"{role}: {text.Trim()}";

    private static string ExtractSupportedText(IEnumerable<AIContentPart>? content)
        => string.Join("\n\n", (content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static IEnumerable<string> ResolveSeedUrls(AIRequest request, WebCrawlerAPIProviderMetadata metadata)
    {
        foreach (var url in metadata.Urls ?? [])
        {
            if (!string.IsNullOrWhiteSpace(url))
                yield return url.Trim();
        }

        if (request.Input?.Metadata?.TryGetValue("urls", out var inputUrls) == true)
        {
            foreach (var url in ExtractStringList(inputUrls))
                yield return url;
        }
    }

    private static IEnumerable<string> ExtractStringList(object? value)
    {
        switch (value)
        {
            case IEnumerable<string> strings:
                foreach (var item in strings.Where(item => !string.IsNullOrWhiteSpace(item)))
                    yield return item.Trim();
                break;
            case JsonElement json when json.ValueKind == JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        yield return item.GetString()!.Trim();
                }
                break;
        }
    }

    private static object? TryExtractOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
                return JsonSerializer.Deserialize<object>(element.GetRawText(), WebCrawlerAPIJson);
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, WebCrawlerAPIJson);
            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var schemaElement)
                && schemaElement.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(schemaElement.GetRawText(), WebCrawlerAPIJson);
            }

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(directSchema.GetRawText(), WebCrawlerAPIJson);
            }
        }
        catch
        {
            // ignore schema extraction failures
        }

        return null;
    }

    private AIResponse ToUnifiedResponse(
        WebCrawlerAPIAgentRun run,
        AIRequest request,
        Dictionary<string, object?> payload)
    {
        var text = ExtractAgentOutputText(run);
        var outputItems = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = text,
                        Metadata = run.Data is JsonElement data
                            ? new Dictionary<string, object?> { ["webcrawlerapi.data"] = data.Clone() }
                            : null
                    }
                ]
            }
        };

        outputItems.AddRange(CreateSourceOutputItems(run));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ResolveUnifiedModel(request, run),
            Status = ToUnifiedStatus(run.Status),
            Usage = CreateUsage(run),
            Output = new AIOutput { Items = outputItems },
            Metadata = BuildUnifiedResponseMetadata(request, run, payload)
        };
    }

    private static WebCrawlerAPIAgentRun ParseAgentRun(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("WebCrawlerAPI agent response must be a JSON object.");

        return new WebCrawlerAPIAgentRun
        {
            Id = GetString(root, "id") ?? throw new InvalidOperationException("WebCrawlerAPI agent response did not include an id."),
            Status = GetString(root, "status") ?? "queued",
            Prompt = GetString(root, "prompt"),
            Model = GetString(root, "model"),
            Urls = root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array
                ? urls.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                : [],
            MaxSpendUsd = GetDecimal(root, "max_spend_usd"),
            BalanceUsedUsd = GetDecimal(root, "balance_used_usd"),
            Success = GetBool(root, "success"),
            Error = GetString(root, "error"),
            ErrorReason = GetString(root, "error_reason"),
            CreatedAt = GetString(root, "created_at"),
            UpdatedAt = GetString(root, "updated_at"),
            Data = root.TryGetProperty("data", out var data) && data.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? data.Clone() : null,
            Trace = root.TryGetProperty("trace", out var trace) && trace.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? trace.Clone() : null,
            LlmRequests = root.TryGetProperty("llm_requests", out var llmRequests) && llmRequests.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? llmRequests.Clone() : null,
            Raw = root
        };
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(
        AIRequest request,
        WebCrawlerAPIAgentRun run,
        Dictionary<string, object?> payload)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["webcrawlerapi.run.id"] = run.Id,
            ["webcrawlerapi.run.status"] = run.Status,
            ["webcrawlerapi.run.success"] = run.Success,
            ["webcrawlerapi.run.error"] = run.Error,
            ["webcrawlerapi.run.error_reason"] = run.ErrorReason,
            ["webcrawlerapi.run.created_at"] = run.CreatedAt,
            ["webcrawlerapi.run.updated_at"] = run.UpdatedAt,
            ["webcrawlerapi.run.max_spend_usd"] = run.MaxSpendUsd,
            ["webcrawlerapi.run.balance_used_usd"] = run.BalanceUsedUsd,
            ["webcrawlerapi.run.urls"] = run.Urls,
            ["webcrawlerapi.request.payload"] = payload,
            ["webcrawlerapi.request.prompt"] = BuildAgentPrompt(request),
            ["webcrawlerapi.response.raw"] = run.Raw.Clone(),
            ["gateway"] = CreateGatewayCostMetadata(run)
        };

        if (run.Data is JsonElement data)
        {
            metadata["webcrawlerapi.run.data"] = data.Clone();
            metadata["webcrawlerapi.structured_output"] = data.Clone();
        }

        if (run.Trace is JsonElement trace)
            metadata["webcrawlerapi.run.trace"] = trace.Clone();

        if (run.LlmRequests is JsonElement llmRequests)
            metadata["webcrawlerapi.run.llm_requests"] = llmRequests.Clone();

        if (run.Urls.Count > 0)
            metadata["webcrawlerapi.sources"] = run.Urls.Select(url => new { url }).ToList();

        return metadata;
    }

    private static Dictionary<string, object?> BuildQueuedRunMetadata(
        WebCrawlerAPIAgentRun run,
        Dictionary<string, object?> payload)
        => new()
        {
            ["webcrawlerapi.run.id"] = run.Id,
            ["webcrawlerapi.run.status"] = run.Status,
            ["webcrawlerapi.run.created_at"] = run.CreatedAt,
            ["webcrawlerapi.run.updated_at"] = run.UpdatedAt,
            ["webcrawlerapi.request.payload"] = payload,
            ["webcrawlerapi.response.raw"] = run.Raw.Clone()
        };

    private static IEnumerable<AIOutputItem> CreateSourceOutputItems(WebCrawlerAPIAgentRun run)
    {
        foreach (var url in run.Urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new AIOutputItem
            {
                Type = "source-url",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = url
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.source.url"] = url,
                    ["chatcompletions.source.title"] = url,
                    ["messages.source.url"] = url,
                    ["messages.source.title"] = url,
                    ["webcrawlerapi.source.type"] = "seed_url"
                }
            };
        }
    }

    private static IEnumerable<AIStreamEvent> CreateSourceStreamEvents(
        string providerId,
        WebCrawlerAPIAgentRun run,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        foreach (var url in run.Urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return CreateStreamEvent(
                providerId,
                "source-url",
                url,
                new AISourceUrlEventData
                {
                    SourceId = url,
                    Url = url,
                    Title = url,
                    Type = "url_citation",
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object?>
                    {
                        ["type"] = "seed_url",
                        ["run_id"] = run.Id
                    })
                },
                timestamp,
                metadata);
        }
    }

    private static AIStreamEvent CreateFinishStreamEvent(
        string providerId,
        string eventId,
        AIRequest request,
        WebCrawlerAPIAgentRun? run,
        Dictionary<string, object?> payload,
        string finishReason,
        DateTimeOffset timestamp)
    {
        var usage = run is null ? CreateUsage(payload) : CreateUsage(run);
        var metadata = run is null
            ? new Dictionary<string, object?> { ["webcrawlerapi.request.payload"] = payload }
            : BuildUnifiedResponseMetadata(request, run, payload);
        var model = $"{providerId}/{request.Model}";

        return CreateStreamEvent(
            providerId,
            "finish",
            eventId,
            new AIFinishEventData
            {
                FinishReason = finishReason,
                Model = model,
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model,
                    timestamp,
                    usage: usage,
                    inputTokens: 0,
                    outputTokens: 0,
                    totalTokens: 0,
                    temperature: request.Temperature,
                    gateway: run is null ? null : CreateGatewayCostFinishMetadata(run))
            },
            timestamp,
            metadata);
    }

    private static AIStreamEvent CreateStreamEvent(
        string providerId,
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static object CreateUsage(WebCrawlerAPIAgentRun run)
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["max_spend_usd"] = run.MaxSpendUsd,
            ["balance_used_usd"] = run.BalanceUsedUsd,
            ["created_at"] = run.CreatedAt,
            ["updated_at"] = run.UpdatedAt
        };

    private static object CreateUsage(Dictionary<string, object?> payload)
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["max_spend_usd"] = payload.TryGetValue("max_spend_usd", out var maxSpend) ? maxSpend : null
        };

    private static Dictionary<string, object?> CreateGatewayCostMetadata(WebCrawlerAPIAgentRun run)
    {
        var gateway = new Dictionary<string, object?>();
        if (run.BalanceUsedUsd is decimal cost)
            gateway["cost"] = cost;
        if (run.MaxSpendUsd is decimal maxSpend)
            gateway["maxSpendUsd"] = maxSpend;
        return gateway;
    }

    private static AIFinishGatewayMetadata? CreateGatewayCostFinishMetadata(WebCrawlerAPIAgentRun run)
    {
        if (run.BalanceUsedUsd is null && run.MaxSpendUsd is null)
            return null;

        var additional = new Dictionary<string, JsonElement>();
        if (run.MaxSpendUsd is decimal maxSpend)
            additional["maxSpendUsd"] = JsonSerializer.SerializeToElement(maxSpend, WebCrawlerAPIJson);

        return new AIFinishGatewayMetadata
        {
            Cost = run.BalanceUsedUsd,
            AdditionalProperties = additional.Count == 0 ? null : additional
        };
    }

    private static object ToTaskData(WebCrawlerAPIAgentRun run)
        => new Dictionary<string, object?>
        {
            ["id"] = run.Id,
            ["status"] = run.Status,
            ["success"] = run.Success,
            ["error"] = run.Error,
            ["errorReason"] = run.ErrorReason,
            ["createdAt"] = run.CreatedAt,
            ["updatedAt"] = run.UpdatedAt,
            ["balanceUsedUsd"] = run.BalanceUsedUsd
        };

    private static string ExtractAgentOutputText(WebCrawlerAPIAgentRun run)
    {
        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
            return GetAgentRunErrorMessage(run);

        if (run.Data is not JsonElement data || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        if (data.ValueKind == JsonValueKind.String)
            return data.GetString() ?? string.Empty;

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("answer", out var answer)
            && answer.ValueKind == JsonValueKind.String)
            return answer.GetString() ?? string.Empty;

        return data.GetRawText();
    }

    private static string GetAgentRunErrorMessage(WebCrawlerAPIAgentRun run)
        => run.Error
           ?? run.ErrorReason
           ?? $"WebCrawlerAPI agent run '{run.Id}' failed with status '{run.Status}'.";

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static string ResolveFinishReason(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop";

    private static string ToUnifiedStatus(string? status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "in_progress";

    private string ResolveUnifiedModel(AIRequest request, WebCrawlerAPIAgentRun? run)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
            return request.Model!;

        if (!string.IsNullOrWhiteSpace(run?.Model))
            return run.Model!.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
                ? run.Model!
                : run.Model!.ToModelId(GetIdentifier());

        return "agent".ToModelId(GetIdentifier());
    }

    private string? ResolveNativeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private static TimeSpan ResolvePollInterval(WebCrawlerAPIProviderMetadata metadata)
        => metadata.PollIntervalSeconds is > 0
            ? TimeSpan.FromSeconds(metadata.PollIntervalSeconds.Value)
            : DefaultPollInterval;

    private static TimeSpan? ResolvePollTimeout(WebCrawlerAPIProviderMetadata metadata)
        => metadata.PollTimeoutSeconds is > 0
            ? TimeSpan.FromSeconds(metadata.PollTimeoutSeconds.Value)
            : DefaultPollTimeout;

    private static bool IsReservedPayloadProperty(string key)
        => key is "prompt" or "max_spend_usd" or "maxSpendUsd" or "urls" or "seed_urls_only" or "seedUrlsOnly" or "output_schema" or "outputSchema" or "model" or "pollIntervalSeconds" or "pollTimeoutSeconds" or "pollMaxAttempts";

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => property.ToString()
        };
    }

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String
            && decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset ParseDateTimeOffsetOrNow(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;

    private static object? ToPlainObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), WebCrawlerAPIJson);

    private static Dictionary<string, Dictionary<string, object>>? CreateScopedProviderMetadata(
        string providerId,
        Dictionary<string, object?> values)
    {
        var filtered = values
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);

        return filtered.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
            {
                [providerId] = filtered
            };
    }

    private sealed class WebCrawlerAPIProviderMetadata
    {
        public decimal? MaxSpendUsd { get; init; }

        public List<string>? Urls { get; init; }

        public bool? SeedUrlsOnly { get; init; }

        public double? PollIntervalSeconds { get; init; }

        public double? PollTimeoutSeconds { get; init; }

        public int? PollMaxAttempts { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed class WebCrawlerAPIAgentRun
    {
        public string Id { get; init; } = default!;
        public string Status { get; init; } = default!;
        public string? Prompt { get; init; }
        public string? Model { get; init; }
        public List<string> Urls { get; init; } = [];
        public decimal? MaxSpendUsd { get; init; }
        public decimal? BalanceUsedUsd { get; init; }
        public bool? Success { get; init; }
        public string? Error { get; init; }
        public string? ErrorReason { get; init; }
        public string? CreatedAt { get; init; }
        public string? UpdatedAt { get; init; }
        public JsonElement? Data { get; init; }
        public JsonElement? Trace { get; init; }
        public JsonElement? LlmRequests { get; init; }
        public JsonElement Raw { get; init; }
    }
}
