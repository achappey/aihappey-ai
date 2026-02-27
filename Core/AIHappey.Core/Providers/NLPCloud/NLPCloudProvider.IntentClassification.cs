using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private sealed record NLPCloudIntentClassificationRequest(
        [property: JsonPropertyName("text")] string Text);

    private sealed record NLPCloudIntentClassificationResponse(
        [property: JsonPropertyName("intent")] string Intent);

    private static string BuildIntentClassificationInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for intent classification.", nameof(messages));
    }

    private async Task<string> SendIntentClassificationAsync(string model, string text, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/intent-classification";
        var payload = new NLPCloudIntentClassificationRequest(text);
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
        var result = await JsonSerializer.DeserializeAsync<NLPCloudIntentClassificationResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.Intent))
            throw new InvalidOperationException("Empty NLPCloud intent classification response.");

        return result.Intent;
    }

    private async IAsyncEnumerable<string> StreamIntentClassificationAsync(
        string model,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendIntentClassificationAsync(model, text, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }
}
