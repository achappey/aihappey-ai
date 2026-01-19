using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
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

        var audioFormat = request.OutputFormat?.ToLowerInvariant().Contains("pcm",
            StringComparison.InvariantCultureIgnoreCase) == true ? "pcm_16000" : "wav_16000";

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
        };

        if (!string.IsNullOrWhiteSpace(audioFormat))
            payload["output_format"] = audioFormat;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "generate/" + request.Model)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = audioFormat.StartsWith("pcm") ? "application/octet-stream" : "audio/mpeg";
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = audioFormat.Split("_").First(),
            },
            Warnings = [],
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model }
        };
    }

}

