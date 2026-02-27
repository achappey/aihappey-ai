using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private sealed record NLPCloudParaphrasingRequest(
        [property: JsonPropertyName("text")] string Text);

    private sealed record NLPCloudParaphrasingResponse(
        [property: JsonPropertyName("paraphrased_text")] string ParaphrasedText);

    private static string BuildParaphrasingInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for paraphrasing.", nameof(messages));
    }

    private async Task<string> SendParaphrasingAsync(string model, string text, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/paraphrasing";
        var payload = new NLPCloudParaphrasingRequest(text);
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
        var result = await JsonSerializer.DeserializeAsync<NLPCloudParaphrasingResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.ParaphrasedText))
            throw new InvalidOperationException("Empty NLPCloud paraphrasing response.");

        return result.ParaphrasedText;
    }

    private async IAsyncEnumerable<string> StreamParaphrasingAsync(
        string model,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendParaphrasingAsync(model, text, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }
}
