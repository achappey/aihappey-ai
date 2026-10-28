using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Zoom;

public sealed partial class ZoomProvider
{
    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => ExecuteSummaryAsync(request, false, cancellationToken);

    private async Task<AIResponse> ExecuteSummaryAsync(AIRequest request, bool vercelDefaultTemperature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (NativeModel(request.Model) != "summarizer")
            throw new NotSupportedException("Zoom conversation endpoints support only the special model 'zoom/summarizer'.");
        if (!string.IsNullOrWhiteSpace(request.Instructions) || request.Tools?.Count > 0
            || request.ToolChoice is not null || request.MaxToolCalls is not null || request.ParallelToolCalls is not null
            || (request.Temperature is not null && !(vercelDefaultTemperature && request.Temperature == 1))
            || request.TopP is not null || request.MaxOutputTokens is not null
            || request.ResponseFormat is not null || request.Verbosity is not null)
            throw new NotSupportedException("Zoom Summarizer does not support instructions, tools, sampling, output-token limits or response formatting. Use Zoom summarization options instead.");

        var transcript = Transcript(request);
        if (Encoding.UTF8.GetByteCount(transcript) > 96 * 1024)
            throw new ArgumentException("Zoom Summarizer transcript text must not exceed 96 KB.");
        var options = Options(request.Metadata?.TryGetValue(GetIdentifier(), out var scoped) == true
            ? JsonSerializer.SerializeToElement(scoped, Json) : null);
        RequireOnly(options, "summary_type", "task", "language", "output_format", "reference_id");
        var config = new JsonObject
        {
            ["summary_type"] = "CONVERSATION",
            ["task"] = "full_summary",
            ["output_format"] = "text"
        };
        foreach (var option in options.Where(option => option.Key != "reference_id"))
        {
            if (option.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                throw new ArgumentException($"Zoom Summarizer '{option.Key}' must be a nonempty string.");
            config[option.Key] = text;
        }
        var task = config["task"]!.GetValue<string>();
        if (task is not ("full_summary" or "recap" or "action_items" or "summary"))
            throw new ArgumentException("Zoom Summarizer task must be full_summary, recap, action_items or summary.");
        if (!string.Equals(config["summary_type"]!.GetValue<string>(), "CONVERSATION", StringComparison.OrdinalIgnoreCase)
            || config["output_format"]!.GetValue<string>() != "text")
            throw new NotSupportedException("Zoom Summarizer supports CONVERSATION summaries with text output.");
        var payload = new JsonObject
        {
            ["input"] = new JsonObject { ["text"] = transcript },
            ["config"] = config
        };
        if (options.ContainsKey("reference_id"))
        {
            if (options["reference_id"] is not JsonValue reference || !reference.TryGetValue<string>(out _))
                throw new ArgumentException("Zoom Summarizer reference_id must be a string.");
            payload["reference_id"] = options["reference_id"]!.DeepClone();
        }

        var (body, _) = await SendAsync("aiservices/summarizer/summarize", payload, false,
            request.Headers?.Select(header => KeyValuePair.Create(header.Key, (string?)header.Value)), cancellationToken);
        try
        {
            var requestId = RequiredString(body, "request_id");
            RequiredString(body, "model");
            if (RequiredString(body, "task") != task)
                throw new JsonException("Zoom returned a different summarization task.");
            var result = body.GetProperty("result");
            var field = task == "summary" ? "summary_text" : task;
            // Older Zoom deployments return a single rendered result.text field.
            var text = result.TryGetProperty(field, out var output) && output.ValueKind == JsonValueKind.String
                ? output.GetString() : result.TryGetProperty("text", out var legacy) && legacy.ValueKind == JsonValueKind.String
                    ? legacy.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException($"Zoom response is missing result.{field}.");
            var usage = body.GetProperty("usage");
            NonnegativeInteger(usage, "input_units");
            NonnegativeInteger(usage, "output_units");
            if (RequiredString(usage, "unit_type") != "characters")
                throw new JsonException("Zoom Summarizer usage must be measured in characters.");

            var metadata = new Dictionary<string, object?>
            {
                [GetIdentifier()] = body,
                ["finishReason"] = "stop",
                ["id"] = requestId
            };
            return new AIResponse
            {
                ProviderId = GetIdentifier(),
                Model = "zoom/summarizer",
                Status = "completed",
                // Character billing is preserved in Zoom metadata, never invented as token usage.
                Metadata = metadata,
                Output = new AIOutput
                {
                    Items = [new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = text }]
                    }]
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Zoom returned an invalid Summarizer response.", exception);
        }
    }

    private static string Transcript(AIRequest request)
    {
        if (request.Input?.Text is { } direct)
        {
            if (request.Input.Items?.Count > 0)
                throw new ArgumentException("Supply either transcript text or messages, not both.");
            ArgumentException.ThrowIfNullOrWhiteSpace(direct);
            return direct;
        }
        var items = request.Input?.Items;
        if (items?.Any(item => item.Type != "message" || item.Role is not ("user" or "assistant")
            || item.Content?.Any(part => part is not AITextContentPart) == true) == true)
            throw new NotSupportedException("Zoom Summarizer accepts text messages only, not files, images, audio, reasoning or tool inputs.");
        var content = items?.LastOrDefault(item => item.Role == "user")?.Content;
        var text = string.Join("\n", content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Zoom Summarizer requires transcript text in the latest user message.");
        return text;
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, CancellationToken cancellationToken = default)
        => StreamSummaryAsync(request, false, cancellationToken);

    private async IAsyncEnumerable<AIStreamEvent> StreamSummaryAsync(
        AIRequest request, bool vercelDefaultTemperature,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteSummaryAsync(request, vercelDefaultTemperature, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;
        var text = ((AITextContentPart)response.Output!.Items![0].Content![0]).Text;
        var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = response.Metadata![GetIdentifier()]! };
        yield return SummaryEvent("text-start", id, new AITextStartEventData { ProviderMetadata = providerMetadata }, timestamp, response);
        cancellationToken.ThrowIfCancellationRequested();
        yield return SummaryEvent("text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, timestamp, response);
        cancellationToken.ThrowIfCancellationRequested();
        yield return SummaryEvent("text-end", id, new AITextEndEventData { ProviderMetadata = providerMetadata }, timestamp, response);
        cancellationToken.ThrowIfCancellationRequested();
        yield return SummaryEvent("finish", request.Id ?? id, new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, timestamp, response.Usage)
        }, timestamp, response);
    }

    private AIStreamEvent SummaryEvent(string type, string id, object data, DateTimeOffset timestamp, AIResponse response)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data,
                Metadata = response.Metadata,
                Output = type == "finish" ? response.Output : null
            }
        };
}
