using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private static Dictionary<string, object?> CreateResearchPayload(AIRequest request, string model, bool stream)
    {
        var payload = ReadOptions(request);
        payload["model"] = model;
        payload["stream"] = stream;
        payload.TryAdd("input", BuildPromptFromUnifiedRequest(request));
        if (JsonSerializer.SerializeToElement(payload["input"]).ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(JsonSerializer.SerializeToElement(payload["input"]).GetString()))
            throw new ArgumentException("Tavily research requires non-empty input.");
        var schema = TryExtractOutputSchema(request.ResponseFormat);
        if (schema is not null)
            payload.TryAdd("output_schema", schema);
        return payload;
    }

    private async Task<AIResponse> ExecuteResearchAsync(AIRequest request, string model, CancellationToken cancellationToken)
    {
        var payload = CreateResearchPayload(request, model, stream: false);
        // include_usage is a status endpoint query option, not a create-task field.
        var includeUsage = payload.Remove("include_usage", out var usageOption) ? usageOption : null;
        using var httpRequest = CreateJsonRequest("research", payload);
        var queued = await SendJsonAsync(httpRequest, cancellationToken);
        var id = GetString(queued, "request_id")
            ?? throw new InvalidOperationException("Tavily response did not include a request_id.");
        var path = $"research/{Uri.EscapeDataString(id)}";
        if (includeUsage is not null)
            path += $"?include_usage={JsonSerializer.SerializeToElement(includeUsage).ToString().ToLowerInvariant()}";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var poll = new HttpRequestMessage(HttpMethod.Get, path);
            var root = await SendJsonAsync(poll, cancellationToken);
            var status = GetString(root, "status");
            if (status == "completed")
            {
                var content = GetProperty(root, "content");
                var text = content is { ValueKind: JsonValueKind.String }
                    ? content.Value.GetString() ?? string.Empty : content?.GetRawText() ?? string.Empty;
                return CreateResponse(request, model, root, text,
                    GetProperty(root, "sources") is JsonElement sources ? ParseSources(sources) : []);
            }
            if (status == "failed")
                throw new InvalidOperationException($"Tavily research task '{id}' failed: {root.GetRawText()}");
            if (status is not ("pending" or "in_progress"))
                throw new InvalidOperationException($"Unexpected Tavily research status: {root.GetRawText()}");
            await Task.Delay(800, cancellationToken);
        }
    }

    private async IAsyncEnumerable<JsonElement> StreamResearchEventsAsync(Dictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Usage is requested on GET /research/{id}, not in a streaming POST.
        payload.Remove("include_usage");
        using var httpRequest = CreateJsonRequest("research", payload);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Tavily streaming research failed ({(int)response.StatusCode}): {body}",
                null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? eventName = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var frame = data.ToString();
                    data.Clear();
                    if (frame.Trim() == "[DONE]")
                        yield break;
                    using var document = JsonDocument.Parse(frame);
                    var root = document.RootElement;
                    if (GetString(root, "object") == "error" || eventName == "error")
                        throw new InvalidOperationException($"Tavily streaming research failed: {root.GetRawText()}");
                    yield return root.Clone();
                }
                if (eventName == "done" || line is null)
                    yield break;
                eventName = null;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line["data:".Length..].TrimStart());
            }
            // SSE comments, retry and id lines do not carry report content.
        }
    }
}
