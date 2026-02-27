using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private const string SummarizationSuffix = "/summarization";

    private enum NLPCloudModelKind
    {
        Chatbot,
        Paraphrasing,
        Summarization,
        IntentClassification,
        CodeGeneration,
        GrammarSpellingCorrection,
        KeywordsKeyphrasesExtraction,
        Translation
    }

    private static NLPCloudModelKind GetModelKind(string model, out string baseModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        const string translationPrefix = "nllb-200-3-3b/translate-to/";
        if (model.StartsWith(translationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            baseModel = "nllb-200-3-3b";
            return NLPCloudModelKind.Translation;
        }

        if (model.EndsWith("/paraphrasing", StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^"/paraphrasing".Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Paraphrasing model is missing base model name.", nameof(model));

            return NLPCloudModelKind.Paraphrasing;
        }

        if (model.EndsWith(SummarizationSuffix, StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^SummarizationSuffix.Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Summarization model is missing base model name.", nameof(model));

            return NLPCloudModelKind.Summarization;
        }

        if (model.EndsWith("/intent-classification", StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^"/intent-classification".Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Intent classification model is missing base model name.", nameof(model));

            return NLPCloudModelKind.IntentClassification;
        }

        if (model.EndsWith("/code-generation", StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^"/code-generation".Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Code generation model is missing base model name.", nameof(model));

            return NLPCloudModelKind.CodeGeneration;
        }

        if (model.EndsWith("/gs-correction", StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^"/gs-correction".Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Grammar and spelling correction model is missing base model name.", nameof(model));

            return NLPCloudModelKind.GrammarSpellingCorrection;
        }

        if (model.EndsWith("/kw-kp-extraction", StringComparison.OrdinalIgnoreCase))
        {
            baseModel = model[..^"/kw-kp-extraction".Length];
            if (string.IsNullOrWhiteSpace(baseModel))
                throw new ArgumentException("Keywords and keyphrases extraction model is missing base model name.", nameof(model));

            return NLPCloudModelKind.KeywordsKeyphrasesExtraction;
        }

        baseModel = model;
        return NLPCloudModelKind.Chatbot;
    }

    private sealed record NLPCloudSummarizationRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Size);

    private sealed record NLPCloudSummarizationResponse(
        [property: JsonPropertyName("summary_text")] string SummaryText);

    private static string BuildSummarizationInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for summarization.", nameof(messages));
    }

    private async Task<string> SendSummarizationAsync(string model, string text, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/summarization";
        var payload = new NLPCloudSummarizationRequest(text, null);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"NLPCloud API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<NLPCloudSummarizationResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.SummaryText))
            throw new InvalidOperationException("Empty NLPCloud summarization response.");

        return result.SummaryText;
    }

    private async IAsyncEnumerable<string> StreamSummarizationAsync(
        string model,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendSummarizationAsync(model, text, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }
}
