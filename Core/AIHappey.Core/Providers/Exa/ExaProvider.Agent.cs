using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    private const string AgentRunsEndpoint = "agent/runs";
    private const string AgentRunToolName = "exa_agent_run";
    private static readonly TimeSpan AgentPollInterval = TimeSpan.FromMilliseconds(500);

    private async Task<AIResponse> ExecuteAgentUnifiedAsync(
        AIRequest request,
        ExaBackendTarget target,
        CancellationToken cancellationToken)
    {
        var payload = BuildAgentPayload(request);
        string? runId = null;
        JsonElement run = default;
        var terminal = false;

        try
        {
            run = await SendAgentJsonAsync(HttpMethod.Post, AgentRunsEndpoint, payload, request, cancellationToken);
            runId = RequireAgentRunId(run);

            while (!IsAgentTerminal(run))
            {
                await Task.Delay(AgentPollInterval, cancellationToken);
                run = await SendAgentJsonAsync(HttpMethod.Get, $"{AgentRunsEndpoint}/{Uri.EscapeDataString(runId)}", null, request, cancellationToken);
            }

            terminal = true;
            return CreateAgentUnifiedResponse(request, target, payload, run);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(runId))
                await CleanupAgentRunSafeAsync(runId, cancelFirst: !terminal, request);
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentUnifiedAsync(
        AIRequest request,
        ExaBackendTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildAgentPayload(request);
        var providerId = GetIdentifier();
        var timestamp = DateTimeOffset.UtcNow;
        var toolCallId = $"exa_agent_run_{Guid.NewGuid():N}";
        var metadata = CreateAgentMetadata(request, payload, null);
        string? runId = null;
        JsonElement terminalRun = default;
        var terminal = false;

        yield return CreateUnifiedStreamEvent(providerId, toolCallId, "tool-input-start", new AIToolInputStartEventData
        {
            ToolName = AgentRunToolName,
            Title = "Exa Agent run",
            ProviderExecuted = true,
            ProviderMetadata = CreateAgentToolProviderMetadata(null, "input")
        }, timestamp, metadata);

        yield return CreateUnifiedStreamEvent(providerId, toolCallId, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = AgentRunToolName,
            Title = "Exa Agent run",
            Input = JsonSerializer.SerializeToElement(payload, JsonWeb),
            ProviderExecuted = true,
            ProviderMetadata = CreateAgentToolProviderMetadata(null, "input")
        }, timestamp, metadata);

        try
        {
            using var httpRequest = CreateAgentRequest(HttpMethod.Post, AgentRunsEndpoint, payload, request, stream: true);
            using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Exa Agent stream failed ({(int)response.StatusCode}): {error}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(responseStream);
            await using var captureSink = AIHappey.Abstractions.Http.ProviderBackendCapture.BeginStreamCapture(
                "exa-agent", response, GetExaBackendCapture(request, providerId));

            string? eventName = null;
            var dataLines = new List<string>();
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is not null && captureSink is not null)
                    await captureSink.WriteLineAsync(line, cancellationToken);

                if (line is not null && line.Length > 0)
                {
                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                        eventName = line["event:".Length..].Trim();
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        dataLines.Add(line["data:".Length..].TrimStart());
                    continue;
                }

                if (dataLines.Count > 0)
                {
                    using var eventDocument = JsonDocument.Parse(string.Join("\n", dataLines));
                    var eventData = eventDocument.RootElement.Clone();
                    runId ??= TryGetString(eventData, "id");
                    var status = TryGetString(eventData, "status") ?? AgentStatusFromEvent(eventName);
                    metadata = CreateAgentMetadata(request, payload, eventData);

                    yield return CreateUnifiedStreamEvent(providerId, toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                    {
                        ToolName = AgentRunToolName,
                        Output = CreateAgentToolOutput(eventData, status),
                        ProviderExecuted = true,
                        Preliminary = !IsAgentTerminalStatus(status),
                        Dynamic = true,
                        ProviderMetadata = CreateAgentToolProviderMetadata(runId, eventName ?? status ?? "event")
                    }, DateTimeOffset.UtcNow, metadata);

                    if (IsAgentTerminalStatus(status))
                    {
                        terminalRun = eventData;
                        terminal = true;
                    }
                }

                eventName = null;
                dataLines.Clear();
                if (line is null || terminal)
                    break;
            }

            if (string.IsNullOrWhiteSpace(runId))
                throw new InvalidOperationException("Exa Agent stream did not return a run ID.");

            // Some terminal SSE events contain only lifecycle fields. Fetch the complete output
            // before deleting the stateless run.
            if (terminalRun.ValueKind != JsonValueKind.Object || !terminalRun.TryGetProperty("output", out _))
                terminalRun = await SendAgentJsonAsync(HttpMethod.Get, $"{AgentRunsEndpoint}/{Uri.EscapeDataString(runId)}", null, request, cancellationToken);

            terminal = IsAgentTerminal(terminalRun);
            metadata = CreateAgentMetadata(request, payload, terminalRun);
            var statusFinal = TryGetString(terminalRun, "status") ?? "failed";
            var failed = statusFinal is "failed" or "cancelled";

            if (failed)
            {
                yield return CreateUnifiedStreamEvent(providerId, toolCallId, "tool-output-error", new AIToolOutputErrorEventData
                {
                    ToolCallId = toolCallId,
                    ErrorText = ExtractAgentError(terminalRun) ?? $"Exa Agent run ended with status '{statusFinal}'.",
                    ProviderExecuted = true,
                    Dynamic = true,
                    ProviderMetadata = CreateAgentToolProviderMetadata(runId, statusFinal)
                }, DateTimeOffset.UtcNow, metadata);
            }

            var text = ExtractAgentOutputText(terminalRun);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return CreateUnifiedStreamEvent(providerId, runId, "text-start", new AITextStartEventData(), DateTimeOffset.UtcNow, metadata);
                yield return CreateUnifiedStreamEvent(providerId, runId, "text-delta", new AITextDeltaEventData { Delta = text }, DateTimeOffset.UtcNow, metadata);
                yield return CreateUnifiedStreamEvent(providerId, runId, "text-end", new AITextEndEventData(), DateTimeOffset.UtcNow, metadata);
            }

            var emittedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in CreateAgentGroundingStreamEvents(providerId, runId, metadata, terminalRun, emittedSources))
                yield return source;

            var usage = CloneProperty(terminalRun, "usage");
            var cost = CloneProperty(terminalRun, "costDollars");
            yield return CreateUnifiedStreamEvent(providerId, runId, "finish", new AIFinishEventData
            {
                FinishReason = failed ? "error" : "stop",
                Model = ToProviderModelId(request.Model),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    ToProviderModelId(request.Model),
                    DateTimeOffset.UtcNow,
                    usage: usage,
                    gateway: CreateGatewayMetadata(cost))
            }, DateTimeOffset.UtcNow, metadata);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(runId))
                await CleanupAgentRunSafeAsync(runId, cancelFirst: !terminal, request);
        }
    }

    private JsonObject BuildAgentPayload(AIRequest request)
    {
        var query = GetLatestAgentUserQuery(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Exa Agent requires a non-empty latest user message or input text.");

        var payload = GetExaProviderOptions(request.Metadata);
        payload.Remove("capture");
        payload.Remove("backend_capture");
        payload.Remove("exaBeta");
        payload.Remove("exa_beta");
        payload.Remove("beta");
        payload.Remove("previousRunId");
        payload["query"] = query;

        var systemPrompt = BuildUnifiedSystemPrompt(request);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            payload["systemPrompt"] = systemPrompt;

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        if (outputSchema is not null)
            payload["outputSchema"] = JsonSerializer.SerializeToNode(outputSchema, JsonWeb);

        return payload;
    }

    private JsonObject GetExaProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(GetIdentifier(), out var raw) || raw is null)
            return new JsonObject();

        var node = raw switch
        {
            JsonElement element => JsonElementObjectToJsonObject(element),
            JsonObject jsonObject => jsonObject.DeepClone() as JsonObject,
            _ => JsonSerializer.SerializeToNode(raw, JsonWeb) as JsonObject
        };
        return node ?? new JsonObject();
    }

    private static string GetLatestAgentUserQuery(AIRequest request)
    {
        var latestUser = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => ExtractUnifiedText(item.Content))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return !string.IsNullOrWhiteSpace(latestUser) ? latestUser! : request.Input?.Text ?? string.Empty;
    }

    private HttpRequestMessage CreateAgentRequest(HttpMethod method, string endpoint, JsonObject? payload, AIRequest request, bool stream)
    {
        var message = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
            message.Content = new StringContent(payload.ToJsonString(JsonWeb), Encoding.UTF8, MediaTypeNames.Application.Json);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : MediaTypeNames.Application.Json));

        var options = GetExaProviderOptions(request.Metadata);
        var beta = options["exaBeta"]?.GetValue<string>()
                   ?? options["exa_beta"]?.GetValue<string>()
                   ?? options["beta"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(beta))
            message.Headers.TryAddWithoutValidation("Exa-Beta", beta);
        return message;
    }

    private async Task<JsonElement> SendAgentJsonAsync(
        HttpMethod method,
        string endpoint,
        JsonObject? payload,
        AIRequest request,
        CancellationToken cancellationToken)
    {
        using var message = CreateAgentRequest(method, endpoint, payload, request, stream: false);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Exa Agent request failed ({(int)response.StatusCode}): {body}");

        await AIHappey.Abstractions.Http.ProviderBackendCapture.CaptureJsonAsync(
            "exa-agent", response, body, GetExaBackendCapture(request, GetIdentifier()), cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private AIResponse CreateAgentUnifiedResponse(AIRequest request, ExaBackendTarget target, JsonObject payload, JsonElement run)
    {
        var status = TryGetString(run, "status") ?? "failed";
        var failed = status is "failed" or "cancelled";
        var text = ExtractAgentOutputText(run);
        var metadata = CreateAgentMetadata(request, payload, run);
        var runId = RequireAgentRunId(run);
        var outputItems = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-output-available",
                        ToolCallId = $"exa_agent_run_{runId}",
                        ToolName = AgentRunToolName,
                        Title = "Exa Agent run",
                        Input = JsonSerializer.SerializeToElement(payload, JsonWeb),
                        Output = CreateAgentToolOutput(run, status),
                        ProviderExecuted = true,
                        State = failed ? "output-error" : "output-available",
                        Metadata = metadata
                    }
                ],
                Metadata = metadata
            },
            CreateMessageOutputItem(text, CloneProperty(run, "output"))
        };
        outputItems.AddRange(CreateAgentGroundingOutputItems(run));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ToProviderModelId(request.Model),
            Status = failed ? "failed" : "completed",
            Usage = CloneProperty(run, "usage"),
            Metadata = metadata,
            Output = new AIOutput { Items = outputItems, Metadata = metadata }
        };
    }

    private static object CreateAgentToolOutput(JsonElement run, string? status)
        => new
        {
            id = TryGetString(run, "id"),
            status,
            stopReason = TryGetString(run, "stopReason"),
            run = run.Clone()
        };

    private Dictionary<string, object?> CreateAgentMetadata(AIRequest request, JsonObject payload, JsonElement? run)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["exa.backend"] = "agent",
            ["exa.model"] = AgentModelId,
            ["exa.request.payload"] = JsonSerializer.SerializeToElement(payload, JsonWeb),
            ["exa.requested_model"] = request.Model
        };
        if (run is { ValueKind: JsonValueKind.Object } value)
        {
            metadata["exa.agent.run"] = value.Clone();
            metadata["exa.agent.run_id"] = TryGetString(value, "id");
            metadata["exa.agent.status"] = TryGetString(value, "status");
            metadata["exa.agent.stop_reason"] = TryGetString(value, "stopReason");
            metadata["exa.costDollars"] = CloneProperty(value, "costDollars");
            metadata["exa.grounding"] = value.TryGetProperty("output", out var output) ? CloneProperty(output, "grounding") : null;
        }
        return metadata;
    }

    private Dictionary<string, Dictionary<string, object>> CreateAgentToolProviderMetadata(string? runId, string stage)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object>
            {
                ["type"] = AgentRunToolName,
                ["run_id"] = runId ?? string.Empty,
                ["stage"] = stage
            }
        };

    private static string ExtractAgentOutputText(JsonElement run)
    {
        if (!run.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
            return string.Empty;
        var text = TryGetString(output, "text");
        if (!string.IsNullOrWhiteSpace(text))
            return text!;
        return output.TryGetProperty("structured", out var structured) && structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? structured.GetRawText()
            : string.Empty;
    }

    private static string? ExtractAgentError(JsonElement run)
    {
        if (run.TryGetProperty("error", out var error))
            return error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
        return null;
    }

    private static IEnumerable<AIOutputItem> CreateAgentGroundingOutputItems(JsonElement run)
    {
        foreach (var citation in EnumerateAgentCitations(run))
        {
            var url = TryGetString(citation, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var title = TryGetString(citation, "title") ?? url;
            yield return new AIOutputItem
            {
                Type = "source-url",
                Content = [new AITextContentPart {
                    Type = "text",
                        Text = title!, Metadata = new() { ["url"] = url, ["raw"] = citation.Clone() } }],
                Metadata = new() { ["url"] = url, ["title"] = title, ["source.raw"] = citation.Clone() }
            };
        }
    }

    private static IEnumerable<AIStreamEvent> CreateAgentGroundingStreamEvents(
        string providerId,
        string eventId,
        Dictionary<string, object?> metadata,
        JsonElement run,
        HashSet<string> emitted)
    {
        foreach (var citation in EnumerateAgentCitations(run))
        {
            var url = TryGetString(citation, "url");
            if (string.IsNullOrWhiteSpace(url) || !emitted.Add(url))
                continue;
            yield return CreateUnifiedStreamEvent(providerId, eventId, "source-url", new AISourceUrlEventData
            {
                SourceId = url,
                Url = url,
                Title = TryGetString(citation, "title") ?? url,
                Type = "citation",
                ProviderMetadata = new() { [providerId] = new() { ["raw"] = citation.Clone() } }
            }, DateTimeOffset.UtcNow, metadata);
        }
    }

    private static IEnumerable<JsonElement> EnumerateAgentCitations(JsonElement run)
    {
        if (!run.TryGetProperty("output", out var output)
            || !output.TryGetProperty("grounding", out var grounding)
            || grounding.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in grounding.EnumerateArray())
        {
            if (!item.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var citation in citations.EnumerateArray())
                yield return citation.Clone();
        }
    }

    private static string RequireAgentRunId(JsonElement run)
        => TryGetString(run, "id") is { Length: > 0 } id
            ? id
            : throw new InvalidOperationException("Exa Agent did not return a run ID.");

    private static bool IsAgentTerminal(JsonElement run)
        => IsAgentTerminalStatus(TryGetString(run, "status"));

    private static bool IsAgentTerminalStatus(string? status)
        => status is "completed" or "failed" or "cancelled";

    private static string? AgentStatusFromEvent(string? eventName)
        => eventName?.StartsWith("agent_run.", StringComparison.OrdinalIgnoreCase) == true
            ? eventName["agent_run.".Length..]
            : null;

    private async Task CleanupAgentRunSafeAsync(string runId, bool cancelFirst, AIRequest request)
    {
        if (cancelFirst)
        {
            try
            {
                await SendAgentJsonAsync(HttpMethod.Post, $"{AgentRunsEndpoint}/{Uri.EscapeDataString(runId)}/cancel", null, request, CancellationToken.None);
            }
            catch
            {
                // Cleanup is best effort; deletion is still attempted.
            }
        }

        try
        {
            await SendAgentJsonAsync(HttpMethod.Delete, $"{AgentRunsEndpoint}/{Uri.EscapeDataString(runId)}", null, request, CancellationToken.None);
        }
        catch
        {
            // Do not mask the result or original provider exception with cleanup failures.
        }
    }
}
