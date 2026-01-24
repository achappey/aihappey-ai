using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private sealed record NLPCloudKeywordsKeyphrasesExtractionRequest(
        [property: JsonPropertyName("text")] string Text);

    private sealed record NLPCloudKeywordsKeyphrasesExtractionResponse(
        [property: JsonPropertyName("keywords_and_keyphrases")] IReadOnlyList<string> KeywordsAndKeyphrases);

    private static string BuildKeywordsKeyphrasesExtractionInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for keywords and keyphrases extraction.", nameof(messages));
    }

    private async Task<IReadOnlyList<string>> SendKeywordsKeyphrasesExtractionAsync(string model, string text, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/kw-kp-extraction";
        var payload = new NLPCloudKeywordsKeyphrasesExtractionRequest(text);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"NLPCloud API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<NLPCloudKeywordsKeyphrasesExtractionResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken).ConfigureAwait(false);

        if (result is null || result.KeywordsAndKeyphrases is null || result.KeywordsAndKeyphrases.Count == 0)
            throw new InvalidOperationException("Empty NLPCloud keywords and keyphrases extraction response.");

        return result.KeywordsAndKeyphrases;
    }

    private async IAsyncEnumerable<string> StreamKeywordsKeyphrasesExtractionAsync(
        string model,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendKeywordsKeyphrasesExtractionAsync(model, text, cancellationToken).ConfigureAwait(false);
        foreach (var keyword in result)
        {
            if (!string.IsNullOrWhiteSpace(keyword))
                yield return keyword;
        }
    }
}
