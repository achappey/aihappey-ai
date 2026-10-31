using AIHappey.Core.AI;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider
{
    private async Task<AIResponse> ExecuteTranslationAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var texts = GetTranslationTexts(request);
        var metadata = GetPaxaMetadata(request);
        ValidatePaxaMetadata(metadata);
        var payload = CopyUnmappedPaxaMetadata(metadata,
            "source", "target", "formality", "borrowed_words", "format", "alternatives",
            "do_not_translate", "instructions", "context", "examples", "glossary", "glossary_mode",
            "output", "text", "model");
        SetJsonValue<object?>(payload, "text", texts.Count == 1 ? texts[0] : texts);
        SetJsonValue(payload, "model", TranslationModelId);
        SetJsonValue(payload, "source", ReadPaxaValue<string>(metadata, "source") ?? "auto");
        SetJsonValue(payload, "target", ReadPaxaValue<string>(metadata, "target") ?? "th");
        SetJsonValue(payload, "formality", ReadPaxaValue<string>(metadata, "formality"));
        SetJsonValue(payload, "borrowed_words", ReadPaxaValue<string>(metadata, "borrowed_words"));
        SetJsonValue(payload, "format", ReadPaxaValue<string>(metadata, "format"));
        SetJsonValue(payload, "alternatives", ReadPaxaValue<int?>(metadata, "alternatives"));
        SetJsonValue(payload, "do_not_translate", ReadPaxaValue<List<string>>(metadata, "do_not_translate"));
        SetJsonValue(payload, "instructions", ReadPaxaValue<string>(metadata, "instructions") ?? request.Instructions);
        SetJsonValue(payload, "context", ReadPaxaValue<string>(metadata, "context"));
        CopyPaxaProperty(metadata, payload, "examples");
        CopyPaxaProperty(metadata, payload, "glossary");
        SetJsonValue(payload, "glossary_mode", ReadPaxaValue<string>(metadata, "glossary_mode"));
        if (TryGetPaxaProperty(metadata, "output", out var output))
            payload["output"] = JsonNode.Parse(output.GetRawText());

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/translate")
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Paxa Labs translation failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var usage = root.TryGetProperty("usage", out var usageElement)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(usageElement.GetRawText(), JsonSerializerOptions.Web) ?? []
            : [];
        var items = new List<AIOutputItem>();
        if (root.TryGetProperty("translations", out var translations) && translations.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var translation in translations.EnumerateArray())
            {
                var text = translation.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
                items.Add(new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content = [new AITextContentPart { Type = "text", Text = text }],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["segmentIndex"] = index++,
                        ["detectedSource"] = translation.TryGetProperty("detected_source", out var source) ? source.GetString() : null,
                        ["alternatives"] = translation.TryGetProperty("alternatives", out var alternatives) ? alternatives.Clone() : null
                    }
                });
            }
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed", Usage = usage,
            Output = new AIOutput { Items = items },
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop", ["source"] = ReadPaxaValue<string>(metadata, "source") ?? "auto",
                ["target"] = ReadPaxaValue<string>(metadata, "target") ?? "th",
                ["response"] = root.Clone()
            }
        };
    }

    private static List<string> GetTranslationTexts(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text)) return [request.Input.Text];
        var texts = request.Input?.Items?.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?.OfType<AITextContentPart>().Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        if (texts.Count == 0) throw new ArgumentException("Paxa Labs translation requires text in the latest user message.", nameof(request));
        return texts;
    }
}
