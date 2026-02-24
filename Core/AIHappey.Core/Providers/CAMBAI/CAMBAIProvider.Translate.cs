using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{
    private const string TranslationModelPrefix = "translate/source-language/";
    private const string TranslationTargetMarker = "/target-language/";


    private async Task<IReadOnlyList<string>> TranslateTextsFromModelAsync(string model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var normalizedModel = NormalizeModelId(model);
        var (sourceLanguageId, targetLanguageId) = ParseSourceAndTargetLanguageIdsFromTranslationModel(normalizedModel);
        return await TranslateTextsAsync(sourceLanguageId, targetLanguageId, texts, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> TranslateTextsAsync(
        int sourceLanguageId,
        int targetLanguageId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            throw new ArgumentException("At least one input text is required.", nameof(texts));

        var payload = new CambaiTranslateCreateRequest
        {
            SourceLanguage = sourceLanguageId,
            TargetLanguage = targetLanguageId,
            Texts = [.. texts]
        };

        var createJson = await PostJsonAndReadAsync("translate", payload, cancellationToken);
        var create = DeserializeOrThrow<CambaiTaskIdResponse>(createJson, "create translation response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation create response missing task_id: {createJson}");

        var finalStatus = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetTranslationStatusAsync(create.TaskId!, ct),
            isTerminal: s => IsTerminalStatus(s.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(finalStatus.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation failed (task_id={create.TaskId}, status={finalStatus.Status}): {finalStatus.RawJson}");

        if (finalStatus.RunId is null || finalStatus.RunId.Value <= 0)
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation status missing run_id for successful task (task_id={create.TaskId}): {finalStatus.RawJson}");

        var (translatedTexts, resultJson) = await GetTranslationResultAsync(finalStatus.RunId.Value, cancellationToken);
        if (translatedTexts.Count == 0)
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation result returned no texts: {resultJson}");

        return translatedTexts;
    }

    private async Task<CambaiTaskStatusResponse> GetTranslationStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"translate/{Uri.EscapeDataString(taskId)}", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation status failed ({(int)resp.StatusCode}): {json}");

        var status = DeserializeOrThrow<CambaiTaskStatusResponse>(json, "translation status response");
        status.RawJson = json;
        return status;
    }

    private async Task<(IReadOnlyList<string> Texts, string RawJson)> GetTranslationResultAsync(int runId, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"translation-result/{runId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} translation result failed ({(int)resp.StatusCode}): {json}");

        var result = DeserializeOrThrow<CambaiTranslationResultResponse>(json, "translation result response");
        return (result.Texts ?? [], json);
    }

    private static (int SourceLanguageId, int TargetLanguageId) ParseSourceAndTargetLanguageIdsFromTranslationModel(string model)
    {
        if (!model.StartsWith(TranslationModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{nameof(CAMBAI)} translation model '{model}' is not supported.");

        var remainder = model[TranslationModelPrefix.Length..];
        var markerIndex = remainder.IndexOf(TranslationTargetMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            throw new ArgumentException($"Invalid translation model '{model}'.", nameof(model));

        var sourcePart = remainder[..markerIndex];
        var targetPart = remainder[(markerIndex + TranslationTargetMarker.Length)..];

        if (!int.TryParse(sourcePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceLanguageId) || sourceLanguageId <= 0)
            throw new ArgumentException($"Invalid source language id in model '{model}'.", nameof(model));

        if (!int.TryParse(targetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetLanguageId) || targetLanguageId <= 0)
            throw new ArgumentException($"Invalid target language id in model '{model}'.", nameof(model));

        return (sourceLanguageId, targetLanguageId);
    }

    private static IEnumerable<string> ExtractChatMessageTexts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text!;

            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;

            if (!part.TryGetProperty("type", out var typeProp)
                || !string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!part.TryGetProperty("text", out var textProp)
                || textProp.ValueKind != JsonValueKind.String)
                continue;

            var text = textProp.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text!;
        }
    }

    private static List<string> ExtractResponseRequestTexts(ResponseRequest options)
    {
        var texts = new List<string>();

        if (options.Input?.IsText == true)
        {
            if (!string.IsNullOrWhiteSpace(options.Input.Text))
                texts.Add(options.Input.Text!);

            return texts;
        }

        var items = options.Input?.Items;
        if (items is null)
            return texts;

        foreach (var msg in items.OfType<ResponseInputMessage>().Where(m => m.Role == ResponseRole.User))
        {
            if (msg.Content.IsText)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content.Text))
                    texts.Add(msg.Content.Text!);

                continue;
            }

            if (!msg.Content.IsParts)
                continue;

            foreach (var part in msg.Content.Parts!.OfType<InputTextPart>())
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                    texts.Add(part.Text);
            }
        }

        return texts;
    }

    private sealed class CambaiTranslateCreateRequest
    {
        [JsonPropertyName("source_language")]
        public int SourceLanguage { get; set; }

        [JsonPropertyName("target_language")]
        public int TargetLanguage { get; set; }

        [JsonPropertyName("texts")]
        public string[] Texts { get; set; } = [];
    }

    private sealed class CambaiTranslationResultResponse
    {
        [JsonPropertyName("texts")]
        public List<string>? Texts { get; set; }
    }
}

