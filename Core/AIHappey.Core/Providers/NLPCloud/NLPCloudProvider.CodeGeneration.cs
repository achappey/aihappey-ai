using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private sealed record NLPCloudCodeGenerationRequest(
        [property: JsonPropertyName("instruction")] string Instruction);

    private sealed record NLPCloudCodeGenerationResponse(
        [property: JsonPropertyName("generated_code")] string GeneratedCode);

    private static string BuildCodeGenerationInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No instruction provided for code generation.", nameof(messages));
    }

    private async Task<string> SendCodeGenerationAsync(string model, string instruction, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var relativeUrl = $"gpu/{model}/code-generation";
        var payload = new NLPCloudCodeGenerationRequest(instruction);
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
        var result = await JsonSerializer.DeserializeAsync<NLPCloudCodeGenerationResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken).ConfigureAwait(false);

        if (result is null || string.IsNullOrWhiteSpace(result.GeneratedCode))
            throw new InvalidOperationException("Empty NLPCloud code generation response.");

        return result.GeneratedCode;
    }

    private async IAsyncEnumerable<string> StreamCodeGenerationAsync(
        string model,
        string instruction,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendCodeGenerationAsync(model, instruction, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }
}
