using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    private const string DocumentIntelligenceModel = "documentintelligence";
    private const string DocumentIntelligenceAnalysisModel = "prebuilt-layout";
    private const string DocumentIntelligenceToolName = "azure_document_intelligence";
    private const int MaxDocumentIntelligencePollAttempts = 300;

    private async Task<AIResponse> ExecuteDocumentIntelligenceUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsDocumentIntelligenceModel(request.Model))
            throw new NotSupportedException($"Azure unified model '{request.Model}' is not supported.");

        var endpoint = GetDocumentIntelligenceEndpoint()
            ?? throw new InvalidOperationException("Azure Document Intelligence requires Endpoint in the form 'region:resource-name'.");
        var files = GetLatestUserDocumentFiles(request);
        var output = new List<AIOutputItem>(files.Count * 2);
        var pagesProcessed = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var file = NormalizeDocumentFile(files[index], index);
            var result = await AnalyzeDocumentAsync(endpoint, file, cancellationToken);
            pagesProcessed += result["analyzeResult"]?["pages"] is JsonArray pages ? pages.Count : 0;
            var toolCallId = Guid.NewGuid().ToString("n");
            var metadata = CreateDocumentMetadata(file, index);

            output.Add(new AIOutputItem
            {
                Type = "tool-call",
                Role = "assistant",
                Metadata = metadata,
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolCallId,
                        ToolName = DocumentIntelligenceToolName,
                        Title = "Azure Document Intelligence",
                        Input = new { model = DocumentIntelligenceAnalysisModel, filename = file.Filename, media_type = file.MediaType, file_index = index },
                        State = "output-available",
                        ProviderExecuted = true,
                        Output = new CallToolResult
                        {
                            IsError = false,
                            Content = [],
                            StructuredContent = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web)
                        }
                    }
                ]
            });

            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = metadata,
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = result["analyzeResult"]?["content"]?.GetValue<string>() ?? string.Empty
                    }
                ]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{DocumentIntelligenceModel}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new Dictionary<string, object?> { ["pages_processed"] = pagesProcessed },
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["azure.document_intelligence.model"] = DocumentIntelligenceModel,
                ["azure.document_intelligence.file_count"] = files.Count,
                ["azure.document_intelligence.pages_processed"] = pagesProcessed
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamDocumentIntelligenceUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteDocumentIntelligenceUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.UtcNow;
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return CreateDocumentStreamEvent(tool.ToolCallId, "tool-input-available", new AIToolInputAvailableEventData
                {
                    ToolName = tool.ToolName!, Title = tool.Title, Input = tool.Input, ProviderExecuted = true
                }, timestamp, item.Metadata);
                yield return CreateDocumentStreamEvent(tool.ToolCallId, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = tool.ToolName!, Output = tool.Output!, ProviderExecuted = true
                }, timestamp, item.Metadata);
                continue;
            }

            foreach (var text in item.Content?.OfType<AITextContentPart>() ?? [])
            {
                var id = Guid.NewGuid().ToString("n");
                yield return CreateDocumentStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreateDocumentStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, timestamp, item.Metadata);
                yield return CreateDocumentStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, item.Metadata);
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        yield return CreateDocumentStreamEvent(Guid.NewGuid().ToString("n"), "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = completedAt.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, completedAt, response.Usage as Dictionary<string, object?>)
        }, completedAt, response.Metadata);
    }


    private async Task<JsonObject> AnalyzeDocumentAsync(string endpoint, NormalizedDocumentFile file, CancellationToken cancellationToken)
    {
        var uri = $"{endpoint}/documentintelligence/documentModels/{DocumentIntelligenceAnalysisModel}:analyze?api-version=2024-11-30&outputContentFormat=markdown";
        var payload = new JsonObject { ["base64Source"] = file.Base64 };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", GetKey());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Azure Document Intelligence failed for '{file.Filename}' ({(int)response.StatusCode}): {body}");
        string? operationUrl = response.Headers.Location?.ToString();
        if (operationUrl is null
            && response.Headers.TryGetValues("Operation-Location", out var values))
            operationUrl = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(operationUrl))
            throw new InvalidOperationException("Azure Document Intelligence response did not include Operation-Location.");

        for (var attempt = 0; attempt < MaxDocumentIntelligencePollAttempts; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationUrl);
            pollRequest.Headers.Add("Ocp-Apim-Subscription-Key", GetKey());
            using var pollResponse = await _httpClient.SendAsync(pollRequest, cancellationToken);
            var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!pollResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"Azure Document Intelligence polling failed ({(int)pollResponse.StatusCode}): {pollBody}");
            var result = JsonNode.Parse(pollBody) as JsonObject
                ?? throw new InvalidOperationException("Azure Document Intelligence returned invalid JSON.");
            var status = result["status"]?.GetValue<string>();
            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                return result;
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Azure Document Intelligence operation {status}: {pollBody}");
        }

        throw new TimeoutException("Azure Document Intelligence operation did not complete within five minutes.");
    }

    private static bool IsDocumentIntelligenceModel(string? model)
        => string.Equals(model?.Split('/').Last(), DocumentIntelligenceModel, StringComparison.OrdinalIgnoreCase);

    private static List<AIFileContentPart> GetLatestUserDocumentFiles(AIRequest request)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUser?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Azure Document Intelligence requires at least one file in the latest user message.", nameof(request));
        return files;
    }

    private static NormalizedDocumentFile NormalizeDocumentFile(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text.Trim(),
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()?.Trim(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Azure Document Intelligence file {index + 1} is empty.", nameof(file));
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Azure Document Intelligence file {index + 1} cannot be a remote URL.", nameof(file));

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType!;
        var base64 = value;
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma < 0 || !base64[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Azure Document Intelligence file {index + 1} must use a base64 data URL.", nameof(file));
            var header = base64[5..comma];
            var separator = header.IndexOf(';');
            if (separator > 0) mediaType = header[..separator];
            base64 = base64[(comma + 1)..];
        }
        try { _ = Convert.FromBase64String(base64); }
        catch (FormatException exception) { throw new ArgumentException($"Azure Document Intelligence file {index + 1} contains invalid base64 data.", nameof(file), exception); }

        return new NormalizedDocumentFile(file.Filename ?? $"document-{index + 1}", mediaType, base64);
    }

    private static Dictionary<string, object?> CreateDocumentMetadata(NormalizedDocumentFile file, int index) => new()
    {
        ["azure.document_intelligence.model"] = DocumentIntelligenceModel,
        ["azure.document_intelligence.filename"] = file.Filename,
        ["azure.document_intelligence.media_type"] = file.MediaType,
        ["azure.document_intelligence.file_index"] = index
    };

    private AIStreamEvent CreateDocumentStreamEvent(string? id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Id = id, Type = type, Data = data, Timestamp = timestamp, Metadata = metadata }
    };

    private sealed record NormalizedDocumentFile(string Filename, string MediaType, string Base64);
}
