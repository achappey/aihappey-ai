using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.TTSReader;

namespace AIHappey.Core.Providers.TTSReader;

public partial class TTSReaderProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetSpeechProviderMetadata<TTSReaderSpeechProviderMetadata>(GetIdentifier());

        var voiceId = (request.Voice ?? metadata?.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice required", nameof(request));

        // Speechify audio_format: recommend always passing it.
        var audioFormat = "mp3";
        var language = (request.Language ?? metadata?.Language)?.Trim();
        var rate = request.Speed ?? metadata?.Rate;
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language required", nameof(request));

        // Allow providerOptions override of model if desired.
        var model = request.Model.Trim();

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice"] = voiceId,
            ["language"] = language,
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Quality))
            payload["quality"] = metadata?.Quality;

        if (rate is not null)
        {
            payload["rate"] = rate;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "ttsSync")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);


        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"TTSReader TTS failed ({(int)resp.StatusCode}): {System.Text.Encoding.UTF8.GetString(bytes)}");

        var mime = "audio/mpeg";
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = "mp3",
            },
            Warnings = [],
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model }
        };
    }

}

