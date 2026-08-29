using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers.PaxaLabs;
using System.Text.Json;
using System.Text;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider
{
    private async Task<AIResponse> ExecuteOcrAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var files = request.Input?.Items?.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count == 0) throw new ArgumentException("Paxa Labs OCR requires at least one file in the latest user message.", nameof(request));

        var options = request.Metadata.GetProviderMetadata<PaxaLabsProviderMetadata>(GetIdentifier());
        var outputFormat = string.IsNullOrWhiteSpace(options?.Output) ? "markdown" : options.Output!;
        if (outputFormat is not ("markdown" or "structured")) throw new ArgumentException("Paxa Labs OCR output must be 'markdown' or 'structured'.", nameof(request));
        var outputItems = new List<AIOutputItem>();
        decimal pages = 0, credits = 0;
        ApplyAuthHeader();

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var base64 = NormalizeOcrBase64(files[fileIndex], fileIndex);
            var payload = new { document = base64, model = OcrModelId, output = outputFormat };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ocr")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
            };
            using var response = await _client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Paxa Labs OCR failed for file {fileIndex + 1} ({(int)response.StatusCode}): {body}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("pages", out var p) && p.TryGetDecimal(out var pv)) pages += pv;
                if (usage.TryGetProperty("credits", out var c) && c.TryGetDecimal(out var cv)) credits += cv;
            }
            if (!root.TryGetProperty("pages", out var pageArray) || pageArray.ValueKind != JsonValueKind.Array) continue;
            foreach (var page in pageArray.EnumerateArray())
            {
                var pageNumber = page.TryGetProperty("page", out var number) ? number.GetInt32() : 0;
                var text = outputFormat == "markdown" && page.TryGetProperty("markdown", out var markdown)
                    ? markdown.GetString() ?? string.Empty
                    : page.GetRawText();
                outputItems.Add(new AIOutputItem
                {
                    Type = "message", Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = text }],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["filename"] = files[fileIndex].Filename, ["fileIndex"] = fileIndex, ["page"] = pageNumber,
                        ["output"] = outputFormat, ["responsePage"] = page.Clone()
                    }
                });
            }
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
            Output = new AIOutput { Items = outputItems },
            Usage = new Dictionary<string, object?> { ["pages"] = pages, ["credits"] = credits },
            Metadata = new Dictionary<string, object?> { ["finishReason"] = "stop", ["fileCount"] = files.Count, ["output"] = outputFormat }
        };
    }

    private static string NormalizeOcrBase64(AIFileContentPart file, int index)
    {
        var value = file.Data switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Paxa Labs OCR file {index + 1} is empty.");
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Paxa Labs OCR does not accept remote URLs.");
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("OCR file data URL must be base64 encoded.");
            value = value[(comma + 1)..];
        }
        try { _ = Convert.FromBase64String(value); } catch (FormatException ex) { throw new ArgumentException($"Paxa Labs OCR file {index + 1} contains invalid base64.", ex); }
        return value;
    }

}
