using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.NaceAI;

/// <summary>
/// Drex browser runs as a stateless special model: POST /v1/browser/runs starts a run, GET /v1/browser/runs/{id}
/// is polled until it is terminal, and request cancellation cancels the run. Nothing is stored in the gateway;
/// providerOptions.naceai.run_id resumes polling a run that was started earlier.
/// </summary>
public sealed partial class NaceAIProvider
{
    private const string BrowserRunModel = "browser-run";
    private const string BrowserRunToolName = "naceai_browser_run";
    private const string BrowserRunsPath = "v1/browser/runs";
    private static readonly TimeSpan BrowserRunTimeout = TimeSpan.FromMinutes(35);
    private static readonly string[] BrowserRunOptions =
        ["start_url", "allowed_origins", "answer_schema", "limits", "artifacts", "risky_actions", "run_id"];

    private static bool IsBrowserRunModel(string? model)
        => string.Equals(NormalizeModelId(model), BrowserRunModel, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalBrowserStatus(string? status)
        => status is "succeeded" or "partial" or "failed" or "timed_out" or "cancelled" or "canceled";

    private static bool IsSuccessfulBrowserRun(JsonObject run)
        => run["status"]?.GetValue<string>() is "succeeded" or "partial";

    private async Task<AIResponse> ExecuteBrowserRunUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        JsonObject? last = null;
        await foreach (var run in PollBrowserRunAsync(request, cancellationToken))
            last = run;

        return CreateBrowserRunResponse(request, last ?? throw new InvalidOperationException("NaceAI returned no browser run."));
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamBrowserRunUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        JsonObject? last = null;
        var toolCallId = $"naceai_browser_run_{Guid.NewGuid():n}";
        var seenSteps = -1;
        await foreach (var run in PollBrowserRunAsync(request, cancellationToken))
        {
            if (last is null)
                yield return StreamEvent(toolCallId, "tool-input-available", new AIToolInputAvailableEventData
                {
                    ToolName = BrowserRunToolName,
                    Title = "NaceAI browser run",
                    Input = new { run_id = run["id"]?.GetValue<string>(), task = run["task"]?.GetValue<string>(), start_url = run["start_url"]?.GetValue<string>() },
                    ProviderExecuted = true
                }, DateTimeOffset.UtcNow);

            last = run;
            var steps = (run["steps"] as JsonArray)?.Count ?? 0;
            if (!IsTerminalBrowserStatus(run["status"]?.GetValue<string>()) && steps != seenSteps)
            {
                seenSteps = steps;
                yield return StreamEvent(toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = BrowserRunToolName,
                    Output = new CallToolResult { IsError = false, Content = [], StructuredContent = JsonSerializer.SerializeToElement(run) },
                    ProviderExecuted = true,
                    Preliminary = true
                }, DateTimeOffset.UtcNow);
            }
        }

        var response = CreateBrowserRunResponse(request, last ?? throw new InvalidOperationException("NaceAI returned no browser run."), toolCallId);
        foreach (var streamEvent in ToStreamEvents(response, request.Model, BrowserRunToolName).Where(e => e.Event.Type != "tool-input-available"))
            yield return streamEvent;
    }

    private async IAsyncEnumerable<JsonObject> PollBrowserRunAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var options = ReadBrowserRunOptions(providerOptions);
        ApplyBrowserRunResponseFormat(request.ResponseFormat, options);

        string? runId = options["run_id"]?.GetValue<string>();
        JsonObject run;
        if (runId is null)
        {
            var body = BuildBrowserRunBody(request, options);
            (run, _) = await SendNaceJsonAsync(HttpMethod.Post, BrowserRunsPath, body, "browser run start", null, cancellationToken);
            runId = run["id"]?.GetValue<string>() ?? throw new InvalidOperationException("NaceAI browser run start did not return an id.");
        }
        else
            (run, _) = await SendNaceJsonAsync(HttpMethod.Get, $"{BrowserRunsPath}/{Uri.EscapeDataString(runId)}", null, "browser run status", null, cancellationToken);

        yield return run;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowserRunTimeout);
        var delay = TimeSpan.FromSeconds(2);
        while (!IsTerminalBrowserStatus(run["status"]?.GetValue<string>()))
        {
            try
            {
                await Task.Delay(delay, timeout.Token);
                (run, _) = await SendNaceJsonAsync(HttpMethod.Get, $"{BrowserRunsPath}/{Uri.EscapeDataString(runId)}", null,
                    "browser run status", null, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await CancelBrowserRunAsync(runId);
                throw;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.25, 5000));
            yield return run;
        }
    }

    private static JsonObject ReadBrowserRunOptions(JsonElement providerOptions)
    {
        var options = new JsonObject();
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return options;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (!BrowserRunOptions.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"NaceAI browser runs do not support provider option '{property.Name}'.");
            options[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return options;
    }

    private static void ApplyBrowserRunResponseFormat(object? responseFormat, JsonObject options)
    {
        if (responseFormat is null || options["answer_schema"] is not null)
            return;
        var format = JsonSerializer.SerializeToNode(responseFormat, JsonSerializerOptions.Web) as JsonObject;
        var type = format?["type"]?.GetValue<string>();
        if (type is null or "text")
            return;
        if (type != "json_schema")
            throw new ArgumentException("NaceAI browser runs only support text or json_schema response formats.");

        options["answer_schema"] = (format!["json_schema"]?["schema"] ?? format["schema"])?.DeepClone()
            ?? throw new ArgumentException("response_format json_schema requires a schema.");
    }

    private static JsonObject BuildBrowserRunBody(AIRequest request, JsonObject options)
    {
        var userMessage = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var task = string.Join("\n", userMessage?.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []).Trim();
        if (string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("NaceAI browser runs require a task in the latest user message.", nameof(request));
        if (task.Length > 4000)
            throw new ArgumentException("NaceAI browser run tasks must be at most 4000 characters.", nameof(request));

        var startUrl = options["start_url"]?.GetValue<string>()
            ?? task.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim('<', '>', '(', ')', '"', '\'', ',', '.'))
                .FirstOrDefault(word => Uri.TryCreate(word, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new ArgumentException("NaceAI browser runs require providerOptions.naceai.start_url (or an http(s) URL in the task).", nameof(request));

        var body = new JsonObject { ["task"] = task, ["start_url"] = start.ToString() };
        body["allowed_origins"] = options["allowed_origins"]?.DeepClone()
            ?? new JsonArray(start.GetLeftPart(UriPartial.Authority));
        foreach (var name in new[] { "answer_schema", "limits", "artifacts", "risky_actions" })
            if (options[name] is { } value)
                body[name] = value.DeepClone();
        return body;
    }

    private async Task CancelBrowserRunAsync(string runId)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BrowserRunsPath}/{Uri.EscapeDataString(runId)}/cancel");
            using var response = await _client.SendAsync(request, cleanup.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private AIResponse CreateBrowserRunResponse(AIRequest request, JsonObject run, string? toolCallId = null)
    {
        var failed = !IsSuccessfulBrowserRun(run);
        var runId = run["id"]?.GetValue<string>();
        var output = new List<AIOutputItem>
        {
            new()
            {
                Type = "tool-call",
                Role = "assistant",
                Content = [new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = toolCallId ?? $"naceai_browser_run_{runId}",
                    ToolName = BrowserRunToolName,
                    Title = "NaceAI browser run",
                    Input = new { run_id = runId, task = run["task"]?.GetValue<string>(), start_url = run["start_url"]?.GetValue<string>() },
                    State = failed ? "output-error" : "output-available",
                    ProviderExecuted = true,
                    Output = new CallToolResult { IsError = failed, Content = [], StructuredContent = JsonSerializer.SerializeToElement(run) }
                }]
            }
        };

        var text = ReadString(run, "answer_text")
                   ?? (run["answer"] is { } answer ? (answer is JsonValue value && value.TryGetValue<string>(out var s) ? s : answer.ToJsonString()) : null)
                   ?? run["outcome"]?["reason"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(text))
            output.Add(new AIOutputItem { Type = "message", Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = text }] });

        var usage = run["usage"] as JsonObject;
        long Count(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        var input = Count("drex_input_tokens") + Count("llm_input_tokens");
        var outputTokens = Count("llm_output_tokens");

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{BrowserRunModel}",
            Status = failed ? "failed" : "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?>
            {
                ["input_tokens"] = input,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = input + outputTokens,
                ["cost_usd"] = usage?["cost_usd"]?.ToString()
            },
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = failed ? "error" : "stop",
                ["naceai.run_id"] = runId,
                ["naceai.status"] = run["status"]?.GetValue<string>(),
                ["naceai.outcome"] = run["outcome"]?.DeepClone()
            }
        };
    }
}
