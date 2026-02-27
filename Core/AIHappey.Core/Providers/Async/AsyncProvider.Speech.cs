using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Async;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<AsyncSpeechProviderMetadata>(GetIdentifier());
        var voiceId = request.Voice ?? metadata?.Voice?.Id;
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("'voice' (async voice UUID) is required.");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var container = (request.OutputFormat ?? metadata?.OutputFormat?.Container ?? "mp3").Trim().ToLowerInvariant();
        container = container is "mp3" or "wav" or "raw" ? container : "mp3";

        var sampleRate = metadata?.OutputFormat?.SampleRate ?? 44100;
        var bitRate = metadata?.OutputFormat?.BitRate ?? 192000;
        var encoding = metadata?.OutputFormat?.Encoding ?? "pcm_s16le";

        var outputFormat = new Dictionary<string, object?>
        {
            ["container"] = container,
            ["sample_rate"] = sampleRate,
        };

        if (container == "mp3")
        {
            outputFormat["bit_rate"] = bitRate;
        }
        else
        {
            outputFormat["encoding"] = encoding;
        }

        var language = request.Language ?? metadata?.Language;

        var speedControl = metadata?.SpeedControl;
        if (request.Speed is not null)
            speedControl = request.Speed.Value;

        var body = new Dictionary<string, object?>
        {
            ["model_id"] = request.Model,
            ["transcript"] = request.Text,
            ["voice"] = new Dictionary<string, object?>
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["output_format"] = outputFormat,
            ["language"] = string.IsNullOrWhiteSpace(language) ? null : language,
            ["speed_control"] = speedControl,
            ["stability"] = metadata?.Stability,
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var resp = await _client.PostAsync("text_to_speech", content, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"asyncAI TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var mime = container switch
        {
            "wav" => "audio/wav",
            "raw" => "application/octet-stream",
            _ => "audio/mpeg"
        };

        var audio = Convert.ToBase64String(bytes);

        // Echo back the effective knobs used so callers can introspect what was sent.
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model_id"] = JsonSerializer.SerializeToElement(request.Model, JsonSerializerOptions.Web),
            ["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web),
            ["output_format"] = JsonSerializer.SerializeToElement(outputFormat, JsonSerializerOptions.Web),
        };

        if (!string.IsNullOrWhiteSpace(language))
            providerMetadata["language"] = JsonSerializer.SerializeToElement(language, JsonSerializerOptions.Web);
        if (speedControl is not null)
            providerMetadata["speed_control"] = JsonSerializer.SerializeToElement(speedControl, JsonSerializerOptions.Web);
        if (metadata?.Stability is not null)
            providerMetadata["stability"] = JsonSerializer.SerializeToElement(metadata.Stability, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new()
            {
                Base64 = audio,
                MimeType = mime,
                Format = container ?? "mp3"
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }
}

