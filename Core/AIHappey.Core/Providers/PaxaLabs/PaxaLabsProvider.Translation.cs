using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers.PaxaLabs;
using System.Text.Json;
using System.Text;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider
{
    private async Task<AIResponse> ExecuteTranslationAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var texts = GetTranslationTexts(request);
        var options = request.Metadata.GetProviderMetadata<PaxaLabsProviderMetadata>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["text"] = texts.Count == 1 ? texts[0] : texts,
            ["model"] = TranslationModelId,
            ["source"] = options?.Source ?? "auto",
            ["target"] = options?.Target ?? "th",
            ["formality"] = options?.Formality,
            ["borrowed_words"] = options?.BorrowedWords,
            ["format"] = options?.Format,
            ["alternatives"] = options?.Alternatives,
            ["do_not_translate"] = options?.DoNotTranslate,
            ["instructions"] = options?.Instructions ?? request.Instructions,
            ["context"] = options?.Context,
            ["examples"] = options?.Examples,
            ["glossary"] = options?.Glossary,
            ["glossary_mode"] = options?.GlossaryMode
        };

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
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
                ["finishReason"] = "stop", ["source"] = options?.Source ?? "auto", ["target"] = options?.Target ?? "th",
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
