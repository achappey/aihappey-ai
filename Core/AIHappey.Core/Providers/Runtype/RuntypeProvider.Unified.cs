using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var options = Options(request);
        return route.Kind switch
        {
            RouteKind.List => CreateOperationResponse(route, "list_runtype_agents",
                await ListAgentsPage(options, cancellationToken)),
            RouteKind.Status or RouteKind.Detail => await ReadRunAsync(route, request, options, cancellationToken),
            RouteKind.Execute => await ExecuteAgentJson(route, request, options, cancellationToken),
            RouteKind.Events => throw new NotSupportedException("Runtype execution event replay requires the streaming gateway endpoint."),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private async Task<AIResponse> ExecuteAgentJson(Route route, AIRequest request, JsonObject options, CancellationToken ct)
    {
        var body = BuildExecuteBody(request, options, stream: false);
        using var http = CreateRequest(HttpMethod.Post, $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/execute");
        ApplyExecuteHeaders(http, request, options);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        http.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        return CreateExecuteResponse(route, raw, (int)response.StatusCode, Headers(response));
    }

    private AIResponse CreateExecuteResponse(Route route, JsonElement raw, int code, Dictionary<string, object?>? headers = null)
    {
        var metadata = Metadata(raw, headers: headers);
        metadata["runtype.httpStatus"] = code;
        var content = new List<AIContentPart> { IdentityPart(route.AgentId!, raw) };
        var result = Property(raw, "result") ?? Property(raw, "finalOutput") ?? Property(raw, "output");
        var text = result?.ValueKind == JsonValueKind.String ? result.Value.GetString()
            : result is { ValueKind: JsonValueKind.Object } obj ? String(obj, "finalOutput") ?? String(obj, "text") : null;
        if (!string.IsNullOrEmpty(text)) content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        var status = String(raw, "status") ?? (code == 202 ? "queued" : "completed");
        if (Property(raw, "success") is { ValueKind: JsonValueKind.False } && status == "completed") status = "failed";
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = route.Model, Status = status,
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata },
            Usage = Usage(raw), Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var options = Options(request);
        if (route.Kind == RouteKind.Events)
        {
            await foreach (var item in ReplayEvents(route, request, options, cancellationToken)) yield return item;
            yield break;
        }
        if (route.Kind != RouteKind.Execute || Boolean(options, "respondAsync"))
        {
            var result = await ExecuteUnifiedAsync(request, cancellationToken);
            foreach (var evt in ResponseEvents(result)) yield return evt;
            yield break;
        }

        var body = BuildExecuteBody(request, options, stream: true);
        using var http = CreateRequest(HttpMethod.Post, $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/execute");
        ApplyExecuteHeaders(http, request, options);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        http.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted ||
            response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            foreach (var evt in ResponseEvents(CreateExecuteResponse(route, raw, (int)response.StatusCode, Headers(response))))
                yield return evt;
            yield break;
        }

        var state = new StreamState();
        await foreach (var frame in ReadFrames(response, cancellationToken))
            foreach (var evt in MapFrame(route, frame, state, Headers(response)))
                yield return evt;
    }

    private static AIUsage? Usage(JsonElement raw)
    {
        var tokens = Property(raw, "totalTokens") ?? Property(raw, "tokens") ?? Property(raw, "usage");
        if (tokens is null && Property(raw, "result") is { } result) tokens = Property(result, "totalTokens");
        var input = Token(tokens, "input") ?? Token(tokens, "inputTokens");
        var output = Token(tokens, "output") ?? Token(tokens, "outputTokens");
        var total = Token(raw, "totalTokensUsed") ?? Token(tokens, "totalTokens")
            ?? (input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null);
        return total.HasValue || input.HasValue || output.HasValue
            ? new AIUsage { InputTokens = input, OutputTokens = output, TotalTokens = total,
                AdditionalProperties = tokens is { } source ? new() { ["runtype"] = source.Clone() } : null }
            : null;
    }

    private static int? Token(JsonElement? raw, string key)
        => raw is { ValueKind: JsonValueKind.Object } value && Property(value, key) is { ValueKind: JsonValueKind.Number } number
            && number.TryGetInt32(out var count) ? count : null;

    private IEnumerable<AIStreamEvent> ResponseEvents(AIResponse response)
    {
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            switch (part)
            {
                case AIToolCallContentPart tool:
                    yield return Event("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
                    {
                        ToolName = tool.ToolName!, Input = tool.Input ?? new { }, ProviderExecuted = true,
                        ProviderMetadata = Scoped(Serialize(response.Metadata?["runtype.raw"]))
                    }, response.Metadata);
                    yield return Event("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
                    {
                        ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true,
                        ProviderMetadata = Scoped(Serialize(response.Metadata?["runtype.raw"]))
                    }, response.Metadata);
                    break;
                case AITextContentPart text:
                    var id = $"runtype-text-{Guid.NewGuid():N}";
                    yield return Event("text-start", id, new AITextStartEventData(), response.Metadata);
                    yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, response.Metadata);
                    yield return Event("text-end", id, new AITextEndEventData(), response.Metadata);
                    break;
            }
        }
        // This completes the read/dispatch request; it is NOT the completion of a queued or paused execution.
        yield return Event("finish", null, Finish(response.Model!, response.Usage, response.Metadata,
            response.Status is "failed" or "cancelled" ? "error" : "stop"), response.Metadata);
    }

    private static AIFinishEventData Finish(string model, object? usage, Dictionary<string, object?>? metadata, string reason)
    {
        var normalized = usage as AIUsage;
        return new AIFinishEventData
        {
            FinishReason = reason, Model = model, InputTokens = normalized?.InputTokens,
            OutputTokens = normalized?.OutputTokens, TotalTokens = normalized?.TotalTokens,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow, usage,
                inputTokens: normalized?.InputTokens, outputTokens: normalized?.OutputTokens, totalTokens: normalized?.TotalTokens,
                additionalProperties: new Dictionary<string, object?> { ["runtype"] = metadata ?? [] })
        };
    }
}
