using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    private const string OcrEndpoint = "/v1/ocr";
    private const string OcrToolName = "mistral_ocr";

    private bool IsOcrModel(string? model)
        => NormalizeMistralModelId(model).Contains("ocr", StringComparison.OrdinalIgnoreCase);

    private async Task<AIResponse> ExecuteOcrUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = NormalizeMistralModelId(request.Model);
        var files = GetLatestUserOcrFiles(request);
        var output = new List<AIOutputItem>(files.Count * 2);
        var pagesProcessed = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeOcrFile(files[index], index);
            var toolCallId = Guid.NewGuid().ToString("n");
            var safeInput = CreateOcrSafeInput(model, file, index);
            var result = await ProcessOcrFileAsync(request, model, file, cancellationToken);
            pagesProcessed += GetOcrPagesProcessed(result);

            output.Add(new AIOutputItem
            {
                Type = "tool-call",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        ToolCallId = toolCallId,
                        Type = "tool-call",
                        ToolName = OcrToolName,
                        Title = "Mistral OCR",
                        Input = safeInput,
                        State = "output-available",
                        ProviderExecuted = true,
                        Output = CreateOcrToolResult(result)
                    }
                ],
                Metadata = CreateOcrMetadata(model, file, index)
            });

            output.Add(CreateOcrMessage(result, model, file, index));
        }

        var metadata = ModelCostMetadataEnricher.AddCost(
            new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["mistral.requested_model"] = request.Model,
                ["mistral.target_model"] = model,
                ["mistral.ocr.file_count"] = files.Count,
                ["mistral.ocr.pages_processed"] = pagesProcessed
            },
            GetOcrGatewayCost(model, pagesProcessed));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{model}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?> { ["pages_processed"] = pagesProcessed },
            Metadata = metadata
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamOcrUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteOcrUnifiedAsync(request, cancellationToken);

        foreach (var item in response.Output?.Items ?? [])
        {
            var timestamp = DateTimeOffset.UtcNow;
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return CreateStreamEvent(GetIdentifier(), tool.ToolCallId, "tool-input-available",
                    new AIToolInputAvailableEventData
                    {
                        ToolName = tool.ToolName ?? OcrToolName,
                        Title = tool.Title,
                        Input = tool.Input,
                        ProviderExecuted = true
                    }, timestamp, item.Metadata);

                yield return CreateStreamEvent(GetIdentifier(), tool.ToolCallId, "tool-output-available",
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = tool.ToolName ?? OcrToolName,
                        Output = tool.Output!,
                        ProviderExecuted = true
                    }, timestamp, item.Metadata);
                continue;
            }

            var eventId = Guid.NewGuid().ToString("n");
            foreach (var text in (item.Content ?? []).OfType<AITextContentPart>())
            {
                yield return CreateStreamEvent(GetIdentifier(), eventId, "text-start", new AITextStartEventData(), timestamp, item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateStreamEvent(GetIdentifier(), eventId, "text-delta",
                        new AITextDeltaEventData { Delta = text.Text }, timestamp, item.Metadata);
                yield return CreateStreamEvent(GetIdentifier(), eventId, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
            }

            foreach (var image in (item.Content ?? []).OfType<AIFileContentPart>())
            {
                yield return CreateStreamEvent(GetIdentifier(), eventId, "file", new
                {
                    mediaType = image.MediaType,
                    filename = image.Filename,
                    url = image.Data
                }, timestamp, item.Metadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateStreamEvent(GetIdentifier(), Guid.NewGuid().ToString("n"), "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = completedAt.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? request.Model ?? "mistral-ocr",
                    completedAt,
                    response.Usage,
                    gateway: GetOcrFinishGatewayMetadata(response.Metadata))
            }, completedAt, response.Metadata);
    }

    private decimal? GetOcrGatewayCost(string model, int pagesProcessed)
    {
        var pricing = ResolveCatalogPricing(model);
        return pricing is null ? null : pagesProcessed * pricing.Input;
    }

    private static int GetOcrPagesProcessed(JsonObject result)
    {
        if (result["usage_info"] is JsonObject usageInfo
            && usageInfo["pages_processed"] is JsonValue pagesProcessed
            && pagesProcessed.TryGetValue<int>(out var reportedPages)
            && reportedPages >= 0)
        {
            return reportedPages;
        }

        return result["pages"] is JsonArray pages ? pages.Count : 0;
    }

    private static AIFinishGatewayMetadata? GetOcrFinishGatewayMetadata(
        Dictionary<string, object?>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue("gateway", out var gatewayValue)
            || gatewayValue is not Dictionary<string, object?> gateway
            || !gateway.TryGetValue("cost", out var costValue)
            || costValue is not decimal cost)
        {
            return null;
        }

        return new AIFinishGatewayMetadata { Cost = cost };
    }

    private async Task<JsonObject> ProcessOcrFileAsync(
        AIRequest request,
        string model,
        NormalizedOcrFile file,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var document = file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["type"] = "image_url", ["image_url"] = file.DataUrl }
            : new JsonObject { ["type"] = "document_url", ["document_url"] = file.DataUrl };
        var payload = new JsonObject
        {
            ["model"] = model,
            ["document"] = document,
            ["include_image_base64"] = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, OcrEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(MistralJsonSerializerOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mistral OCR failed for '{file.Filename}' ({(int)response.StatusCode}): {body}");

        await ProviderBackendCapture.CaptureJsonAsync(
            "ocr",
            response,
            body,
            request.GetMistralBackendCapture(GetIdentifier()),
            cancellationToken);

        return JsonNode.Parse(body) as JsonObject
            ?? throw new InvalidOperationException("Mistral OCR returned an empty or invalid JSON object.");
    }

    private static List<AIFileContentPart> GetLatestUserOcrFiles(AIRequest request)
    {
        var latestUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUserMessage?.Content?.OfType<AIFileContentPart>().ToList() ?? [];

        if (files.Count == 0)
            throw new ArgumentException("Mistral OCR requires at least one file in the latest user message.", nameof(request));

        return files;
    }

    private static NormalizedOcrFile NormalizeOcrFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => throw new ArgumentException($"Mistral OCR file {index + 1} must contain base64 text or a base64 data URL.", nameof(file))
        };

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Mistral OCR file {index + 1} is empty.", nameof(file));
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Mistral OCR file {index + 1} cannot be a remote URL.", nameof(file));

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType!;
        var base64 = value.Trim();
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Mistral OCR file {index + 1} must use a base64 data URL.", nameof(file));
            var header = base64[5..comma];
            var separator = header.IndexOf(';');
            if (separator > 0)
                mediaType = header[..separator];
            base64 = base64[(comma + 1)..];
        }

        try
        {
            _ = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"Mistral OCR file {index + 1} contains invalid base64 data.", nameof(file), exception);
        }

        var filename = string.IsNullOrWhiteSpace(file.Filename) ? $"document-{index + 1}" : file.Filename!;
        return new NormalizedOcrFile(filename, mediaType, $"data:{mediaType};base64,{base64}");
    }

    private static object CreateOcrSafeInput(string model, NormalizedOcrFile file, int index)
        => new { model, filename = file.Filename, media_type = file.MediaType, file_index = index };

    private static Dictionary<string, object?> CreateOcrMetadata(string model, NormalizedOcrFile file, int index)
        => new()
        {
            ["mistral.ocr.model"] = model,
            ["mistral.ocr.filename"] = file.Filename,
            ["mistral.ocr.media_type"] = file.MediaType,
            ["mistral.ocr.file_index"] = index
        };

    private static CallToolResult CreateOcrToolResult(JsonObject result)
        => new()
        {
            IsError = false,
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(result, MistralJsonSerializerOptions)
        };

    private static AIOutputItem CreateOcrMessage(JsonObject result, string model, NormalizedOcrFile file, int index)
    {
        var content = new List<AIContentPart>();
        var markdown = new List<string>();

        if (result["pages"] is JsonArray pages)
        {
            foreach (var page in pages.OfType<JsonObject>())
            {
                var pageMarkdown = page["markdown"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(pageMarkdown))
                    markdown.Add(pageMarkdown);

                if (page["images"] is not JsonArray images)
                    continue;
                foreach (var image in images.OfType<JsonObject>())
                {
                    var imageBase64 = image["image_base64"]?.GetValue<string>();
                    if (!TryNormalizeReturnedImage(imageBase64, image["id"]?.GetValue<string>(), out var dataUrl, out var mediaType, out var filename))
                        continue;
                    content.Add(new AIFileContentPart
                    {
                        Type = "file",
                        Data = dataUrl,
                        MediaType = mediaType,
                        Filename = filename
                    });
                }
            }
        }

        content.Insert(0, new AITextContentPart { Type = "text", Text = string.Join("\n\n", markdown) });
        return new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content = content,
            Metadata = CreateOcrMetadata(model, file, index)
        };
    }

    private static bool TryNormalizeReturnedImage(
        string? value,
        string? id,
        out string? dataUrl,
        out string mediaType,
        out string filename)
    {
        dataUrl = null;
        filename = string.IsNullOrWhiteSpace(id) ? $"ocr-image-{Guid.NewGuid():n}.png" : id!;
        mediaType = GuessImageMediaType(filename) ?? "image/png";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var base64 = value.Trim();
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return false;
            var header = base64[5..comma];
            var separator = header.IndexOf(';');
            if (separator > 0)
                mediaType = header[..separator];
            base64 = base64[(comma + 1)..];
        }

        try { _ = Convert.FromBase64String(base64); }
        catch (FormatException) { return false; }
        dataUrl = $"data:{mediaType};base64,{base64}";
        return true;
    }

    private sealed record NormalizedOcrFile(string Filename, string MediaType, string DataUrl);
}
