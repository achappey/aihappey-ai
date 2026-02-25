using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Daglo;

public partial class DagloProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new
        {
            text = request.Text
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/v1/sync/audios")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Daglo TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var mimeType = string.IsNullOrWhiteSpace(contentType) ? "audio/wav" : contentType;
        var format = ResolveFormatFromMimeType(mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }

    private static string ResolveFormatFromMimeType(string mimeType)
    {
        var value = mimeType.Trim().ToLowerInvariant();
        if (value.Contains("wav")) return "wav";
        if (value.Contains("mpeg") || value.Contains("mp3")) return "mp3";
        if (value.Contains("ogg")) return "ogg";
        if (value.Contains("flac")) return "flac";
        if (value.Contains("aac")) return "aac";
        if (value.Contains("opus")) return "opus";

        return "wav";
    }
}
