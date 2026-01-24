using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var model = request.Model.Trim();
        if (!string.Equals(model, "speech-t5", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NLPCloud speech only supports the speech-t5 model.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var voice = request.Voice?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(voice) && voice is not "man" and not "woman")
            throw new ArgumentException("voice must be either 'man' or 'woman' for NLPCloud speech.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice"] = voice ?? "woman"
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"gpu/{model}/speech-synthesis")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"NLPCloud speech synthesis failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"NLPCloud speech synthesis response missing url: {body}");

        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("NLPCloud speech synthesis returned an empty url.");

        var downloadClient = _factory.CreateClient();
        using var audioResp = await downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"NLPCloud speech download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        const string mime = "audio/wav";
        const string format = "wav";
        var base64 = Convert.ToBase64String(audioBytes);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = doc.RootElement.Clone()
            }
        };
    }
}
