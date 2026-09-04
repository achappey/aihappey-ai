using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    private bool IsPawaParserModel(string? model)
        => NormalizePawaModelId(model).Contains("parser", StringComparison.OrdinalIgnoreCase);

    private async Task<AIResponse> ExecutePawaDocumentParserAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUser?.Content?.OfType<AIFileContentPart>().Take(26).ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("PawaAI document parsing requires at least one file in the latest user message.", nameof(request));
        if (files.Count > 25)
            throw new ArgumentException("PawaAI document parsing supports at most 25 files.", nameof(request));

        using var form = new MultipartFormDataContent();
        foreach (var (file, index) in files.Select((file, index) => (file, index)))
        {
            var normalized = DecodePawaFile(file, index);
            var content = new ByteArrayContent(normalized.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(normalized.MediaType);
            form.Add(content, "documents", normalized.Filename);
        }
        form.Add(new StringContent(NormalizePawaModelId(request.Model)), "model");
        var prompt = string.Join("\n", latestUser?.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = request.Instructions;
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/documents/parse", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePawaSuccess(response, raw, "document parsing request");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var results = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(item => item.Clone()).ToList()
            : [];

        var output = new List<AIOutputItem>();
        foreach (var (result, index) in results.Select((result, index) => (result, index)))
        {
            var toolCallId = Guid.NewGuid().ToString("N");
            var filename = result.TryGetProperty("fileName", out var fileName) ? fileName.GetString() : files[index].Filename;
            output.Add(new AIOutputItem
            {
                Type = "tool-call",
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = toolCallId,
                        ToolName = "pawa_document_parse",
                        Title = "Pawa AI Document Parse",
                        Input = new { filename, model = NormalizePawaModelId(request.Model) },
                        State = "output-available",
                        ProviderExecuted = true,
                        Output = result
                    }
                ]
            });
            output.Add(new AIOutputItem
            {
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = result.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty,
                        Metadata = new() { ["pawaai.document"] = result }
                    }
                ],
                Metadata = new() { ["pawaai.document"] = result }
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizePawaModelId(request.Model).ToModelId(GetIdentifier()),
            Status = "completed",
            Output = new AIOutput { Items = output, Metadata = new() { ["pawaai.raw"] = root } },
            Metadata = new() { ["pawaai.raw"] = root, ["pawaai.documents"] = results.Count }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamPawaDocumentParserAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecutePawaDocumentParserAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            if (item.Content?.OfType<AIToolCallContentPart>().FirstOrDefault() is { } tool)
            {
                yield return CreatePawaEvent(tool.ToolCallId, "tool-input-available",
                    new AIToolInputAvailableEventData { ToolName = tool.ToolName!, Title = tool.Title, Input = tool.Input, ProviderExecuted = true }, item.Metadata);
                yield return CreatePawaEvent(tool.ToolCallId, "tool-output-available",
                    new AIToolOutputAvailableEventData { ToolName = tool.ToolName!, Output = tool.Output!, ProviderExecuted = true }, item.Metadata);
                continue;
            }

            foreach (var text in item.Content?.OfType<AITextContentPart>() ?? [])
            {
                var responseId = Guid.NewGuid().ToString("N");
                yield return CreatePawaEvent(responseId, "text-start", new AITextStartEventData(), item.Metadata);
                if (!string.IsNullOrEmpty(text.Text))
                    yield return CreatePawaEvent(responseId, "text-delta", new AITextDeltaEventData { Delta = text.Text }, item.Metadata);
                yield return CreatePawaEvent(responseId, "text-end", new AITextEndEventData(), item.Metadata);
            }
        }
        yield return CreatePawaFinishEvent(request, response);
    }
}
