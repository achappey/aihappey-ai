using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private const string OcrToolName = "cortecs_ocr";

    private static string NormalizeOcrModel(string? model)
        => model?.StartsWith("cortecs/", StringComparison.OrdinalIgnoreCase) == true
            ? model["cortecs/".Length..]
            : model ?? string.Empty;

    private static bool IsOcrModel(string? model)
        => NormalizeOcrModel(model).ToLowerInvariant() is "mistral-ocr-4.0" or "mistral-ocr-2512" or "mistral-ocr-4.1";

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = NormalizeOcrModel(request.Model).ToLowerInvariant();
        var userMessage = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = userMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Cortecs OCR requires a file in the latest user message.", nameof(request));

        var providerOptions = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var format = CreateOcrAnnotationFormat(request.ResponseFormat);
        var structured = format is not null || (request.ResponseFormat is null && providerOptions.ValueKind == JsonValueKind.Object
            && providerOptions.TryGetProperty("document_annotation_format", out var rawFormat) && rawFormat.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);
        var prompt = string.Join("\n\n", userMessage?.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
        var output = new List<AIOutputItem>();
        var pagesProcessed = 0;
        decimal credits = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeOcrFile(files[index], index);
            var payload = providerOptions.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(providerOptions.GetRawText()) as JsonObject ?? new JsonObject()
                : new JsonObject();
            // Contract-owned fields cannot be replaced via provider metadata.
            foreach (var key in payload.Select(item => item.Key).Where(key => key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || key.Equals("document", StringComparison.OrdinalIgnoreCase)).ToList())
                payload.Remove(key);
            payload["model"] = model;
            payload["document"] = new JsonObject
            {
                ["type"] = file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image_url" : "document_url",
                [file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image_url" : "document_url"] = file.DataUrl
            };
            if (payload["include_image_base64"] is null)
                payload["include_image_base64"] = true;
            if (request.ResponseFormat is not null)
            {
                payload.Remove("document_annotation_format");
                payload.Remove("document_annotation_prompt");
                if (format is not null)
                {
                    payload["document_annotation_format"] = format.DeepClone();
                    if (!string.IsNullOrWhiteSpace(prompt))
                        payload["document_annotation_prompt"] = prompt;
                }
            }

            var result = await ProcessOcrFileAsync(payload, file.Filename, cancellationToken);
            var toolId = Guid.NewGuid().ToString("n");
            var safeInput = new { model, filename = file.Filename, media_type = file.MediaType, file_index = index };
            output.Add(new AIOutputItem
            {
                Type = "tool-call", Role = "assistant",
                Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = toolId, ToolName = OcrToolName, Title = "Cortecs OCR",
                    Input = safeInput, State = "output-available", ProviderExecuted = true,
                    Output = new CallToolResult { IsError = false, Content = [], StructuredContent = JsonSerializer.SerializeToElement(result) }
                }]
            });
            output.Add(CreateOcrMessage(result, structured));
            pagesProcessed += ReadOcrPages(result);
            if (result["usage_info"] is JsonObject usage && usage["credits"] is JsonValue value
                && decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var reportedCredits))
                credits += reportedCredits;
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"{GetIdentifier()}/{model}", Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?> { ["pages_processed"] = pagesProcessed, ["credits"] = credits },
            Metadata = new Dictionary<string, object?> { ["finishReason"] = "stop", ["cortecs.ocr.pages_processed"] = pagesProcessed }
        };
    }

    private async Task<JsonObject> ProcessOcrFileAsync(JsonObject payload, string filename, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/ocr")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cortecs OCR failed for '{filename}' ({(int)response.StatusCode}): {body}");
        return JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Cortecs OCR returned an invalid JSON object.");
    }

    private static int ReadOcrPages(JsonObject result)
        => result["usage_info"] is JsonObject usage && usage["pages_processed"] is JsonValue count
            && int.TryParse(count.ToString(), out var number) && number >= 0
            ? number : result["pages"] is JsonArray pages ? pages.Count : 0;

    private static JsonObject? CreateOcrAnnotationFormat(object? responseFormat)
    {
        if (responseFormat is null) return null;
        var format = JsonSerializer.SerializeToNode(responseFormat, JsonSerializerOptions.Web) as JsonObject
            ?? throw new ArgumentException("Cortecs OCR response_format must be a JSON object.", nameof(responseFormat));
        var type = format["type"]?.GetValue<string>();
        if (type == "text") return null;
        if (type == "json_object") return new JsonObject { ["type"] = "json_object" };
        if (type == "json_schema" && format["json_schema"] is JsonObject schema
            && !string.IsNullOrWhiteSpace(schema["name"]?.GetValue<string>()) && schema["schema"] is JsonObject)
            return new JsonObject { ["type"] = "json_schema", ["json_schema"] = schema.DeepClone() };
        throw new ArgumentException("Cortecs OCR response_format must be text, json_object, or a named json_schema with a schema object.", nameof(responseFormat));
    }

    private static (string Filename, string MediaType, string DataUrl) NormalizeOcrFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Cortecs OCR file {index + 1} must be base64 data, not a remote URL.", nameof(file));
        var mediaType = file.MediaType ?? "application/octet-stream";
        var base64 = value.Trim();
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Cortecs OCR file {index + 1} must use a base64 data URL.", nameof(file));
            mediaType = base64[5..base64.IndexOf(';')];
            base64 = base64[(comma + 1)..];
        }
        try { _ = Convert.FromBase64String(base64); }
        catch (FormatException ex) { throw new ArgumentException($"Cortecs OCR file {index + 1} contains invalid base64.", nameof(file), ex); }
        return (string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!,
            mediaType, $"data:{mediaType};base64,{base64}");
    }

    private static AIOutputItem CreateOcrMessage(JsonObject result, bool structured)
    {
        var markdown = new List<string>();
        var content = new List<AIContentPart>();
        foreach (var page in (result["pages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var text = page["markdown"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) markdown.Add(text);
            foreach (var image in (page["images"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var raw = image["image_base64"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var filename = image["id"]?.GetValue<string>() ?? $"ocr-image-{Guid.NewGuid():n}.png";
                var mediaType = Path.GetExtension(filename).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png"
                };
                var dataUrl = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? raw : $"data:{mediaType};base64,{raw}";
                content.Add(new AIFileContentPart { Type = "file", Data = dataUrl, MediaType = mediaType, Filename = filename });
            }
        }
        var annotation = result["document_annotation"];
        var outputText = structured ? annotation switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject obj => obj.ToJsonString(),
            _ => throw new InvalidOperationException("Cortecs OCR structured response is missing document_annotation.")
        } : string.Join("\n\n", markdown);
        content.Insert(0, new AITextContentPart { Type = "text", Text = outputText });
        return new AIOutputItem { Type = "message", Role = "assistant", Content = content };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            var now = DateTimeOffset.UtcNow;
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return OcrStreamEvent(tool.ToolCallId, "tool-input-available", new AIToolInputAvailableEventData
                { ToolName = OcrToolName, Title = tool.Title, Input = tool.Input, ProviderExecuted = true }, now);
                yield return OcrStreamEvent(tool.ToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                { ToolName = OcrToolName, Output = tool.Output!, ProviderExecuted = true }, now);
                continue;
            }
            var id = Guid.NewGuid().ToString("n");
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                yield return OcrStreamEvent(id, "text-start", new AITextStartEventData(), now);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return OcrStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, now);
                yield return OcrStreamEvent(id, "text-end", new AITextEndEventData(), now);
            }
            foreach (var image in (item.Content ?? []).OfType<AIFileContentPart>())
                yield return OcrStreamEvent(id, "file", new { mediaType = image.MediaType, filename = image.Filename, url = image.Data }, now);
        }
        var completedAt = DateTimeOffset.UtcNow;
        yield return OcrStreamEvent(Guid.NewGuid().ToString("n"), "finish", new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = completedAt.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? request.Model ?? "cortecs-ocr", completedAt, response.Usage)
        }, completedAt, response.Metadata);
    }

    private AIStreamEvent OcrStreamEvent(string? id, string type, object data, DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
        => new() { ProviderId = GetIdentifier(), Event = new AIEventEnvelope
            { Type = type, Id = id, Timestamp = timestamp, Data = data }, Metadata = metadata };
}
