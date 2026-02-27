using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private sealed record NLPCloudGrammarSpellingCorrectionRequest(
        [property: JsonPropertyName("text")] string Text);

    private sealed record NLPCloudGrammarSpellingCorrectionResponse(
        [property: JsonPropertyName("correction")] string Correction);

    private static string BuildGrammarSpellingCorrectionInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for grammar and spelling correction.", nameof(messages));
    }

    private async Task<string> SendGrammarSpellingCorrectionAsync(string model, string text, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/gs-correction";
        var payload = new NLPCloudGrammarSpellingCorrectionRequest(text);
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
        var result = await JsonSerializer.DeserializeAsync<NLPCloudGrammarSpellingCorrectionResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.Correction))
            throw new InvalidOperationException("Empty NLPCloud grammar and spelling correction response.");

        return result.Correction;
    }

    private async IAsyncEnumerable<string> StreamGrammarSpellingCorrectionAsync(
        string model,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendGrammarSpellingCorrectionAsync(model, text, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }
}
