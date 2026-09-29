using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Amnt;

public partial class AmntProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model is "amnt/amnt-svg-1.1" or "amnt/svg/vectorize")
            return await this.ExecuteUnifiedImageAsync(request, ct);
        var agent = ParseAgent(request.Model);
        var input = BuildAgentInput(request);
        var (raw, headers) = await SendAsync(HttpMethod.Post, "api/v1/run", new { agent, input }, ct);
        if (Property(raw, "success") is not { ValueKind: JsonValueKind.True })
            throw new InvalidOperationException($"Amnt agent run failed: {String(raw, "error") ?? "Unknown error"}");

        var metadata = new Dictionary<string, object?>
        {
            ["amnt.raw"] = raw.Clone(), ["amnt.agent"] = agent, ["amnt.headers"] = headers
        };
        foreach (var name in new[] { "credits_used", "credits_remaining", "receipt" })
            if (Property(raw, name) is { } value) metadata[$"amnt.{name}"] = value;

        var output = Property(raw, "output") ?? throw new InvalidOperationException("Amnt agent returned no output.");
        var parts = new List<AIContentPart>
        {
            new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = $"amnt-{Guid.NewGuid():N}", ToolName = "amnt_run_agent",
                Title = $"Amnt {agent}", Input = input, Output = new CallToolResult { StructuredContent = raw.Clone() },
                ProviderExecuted = true, State = "output-available", Metadata = metadata
            }
        };
        if (String(output, "text") is { } text)
            parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        else if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("images", out var images)
                 && images.ValueKind == JsonValueKind.Array)
            foreach (var image in images.EnumerateArray())
                if (image.ValueKind == JsonValueKind.String && image.GetString() is { } url)
                    parts.Add(new AIFileContentPart { Type = "file", MediaType = "image/png", Data = url, Metadata = metadata });
        else
            parts.Add(new AITextContentPart { Type = "text", Text = output.GetRawText(), Metadata = metadata });

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed", Metadata = metadata,
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }],
                Metadata = metadata
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (request.Model is "amnt/amnt-svg-1.1" or "amnt/svg/vectorize")
        {
            await foreach (var part in this.StreamUnifiedImageAsync(request, ct)) yield return part;
            yield break;
        }
        // Amnt /run is synchronous; stream only the completed run, never issue a second call.
        var result = await ExecuteUnifiedAsync(request, ct);
        var now = DateTimeOffset.UtcNow;
        var id = $"amnt-{Guid.NewGuid():N}";
        var tool = result.Output!.Items![0].Content!.OfType<AIToolCallContentPart>().First();
        yield return AgentEvent("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = tool.ToolName!, Title = tool.Title, Input = tool.Input!, ProviderExecuted = true
        }, now, result.Metadata);
        yield return AgentEvent("tool-output-available", id, new AIToolOutputAvailableEventData
        {
            ToolName = tool.ToolName!, Output = (CallToolResult)tool.Output!, ProviderExecuted = true, Dynamic = true,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = new() { ["raw"] = result.Metadata!["amnt.raw"]! }
            }
        }, now, result.Metadata);
        foreach (var text in result.Output.Items[0].Content.OfType<AITextContentPart>())
        {
            yield return AgentEvent("text-start", id, new AITextStartEventData(), now, result.Metadata);
            yield return AgentEvent("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, now, result.Metadata);
            yield return AgentEvent("text-end", id, new AITextEndEventData(), now, result.Metadata);
        }
        foreach (var file in result.Output.Items[0].Content.OfType<AIFileContentPart>())
            yield return AgentEvent("file", id, new AIFileEventData { MediaType = file.MediaType!, Url = file.Data!.ToString()! }, now, result.Metadata);
        yield return AgentEvent("finish", id, new AIFinishEventData
        {
            FinishReason = "stop", Model = request.Model, CompletedAt = now.ToUnixTimeSeconds(),
            Response = result.Metadata!["amnt.raw"],
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, now,
                additionalProperties: new Dictionary<string, object?> { [GetIdentifier()] = result.Metadata["amnt.raw"] })
        }, now, result.Metadata, result.Output);
    }

    private AIStreamEvent AgentEvent(string type, string id, object data, DateTimeOffset now,
        Dictionary<string, object?>? metadata, AIOutput? output = null) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = now, Output = output }
    };

    private static string ParseAgent(string? model)
    {
        const string prefix = "amnt/agents/";
        var agent = model?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true ? model[prefix.Length..] : null;
        if (!ValidAgentId(agent)) throw new ArgumentException("Expected Amnt model 'amnt/agents/{handle}/{slug}'.", nameof(model));
        return agent!;
    }

    private static JsonElement BuildAgentInput(AIRequest request)
    {
        JsonElement input = default;
        if (request.Metadata?.TryGetValue("amnt", out var scoped) == true && scoped is not null)
        {
            var options = scoped is JsonElement element ? element : JsonSerializer.SerializeToElement(scoped, Json);
            if (options.ValueKind != JsonValueKind.Object) throw new ArgumentException("Amnt metadata must be an object.");
            input = Property(options, "input") ?? options;
        }
        var last = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
        var text = last is null ? request.Input?.Text : string.Join("\n", last.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                input = document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone()
                    : throw new ArgumentException("Amnt agent input JSON must be an object.");
            }
            catch (JsonException) { input = JsonSerializer.SerializeToElement(new { prompt = text }, Json); }
        }
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Amnt agent requires an input object.");
        return input;
    }
}
