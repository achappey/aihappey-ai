using AIHappey.Common.Model.Providers.Novita;
using System.Text.Json;
using System.Text;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider 
{
    private async Task<SpeechResponse> SpeechRequestMiniMax(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata =
            request.GetProviderMetadata<NovitaSpeechProviderMetadata>(GetIdentifier());

        var text = request.Text ?? "";
        if (text.Length > 10_000)
            throw new InvalidOperationException("MiniMax sync TTS text must be < 10,000 characters."); // :contentReference[oaicite:1]{index=1}

        // Endpoint suffix normalization
        var modelPath = request.Model ?? "minimax-speech-2.5-turbo-preview";
        if (modelPath.StartsWith("speech-", StringComparison.OrdinalIgnoreCase))
            modelPath = "minimax-" + modelPath;

        // voice_setting
        var voiceId = request.Voice ?? metadata?.MiniMax?.VoiceId ?? "Friendly_Person"; // system voices listed in docs :contentReference[oaicite:2]{index=2}

        var speed = request.Speed ?? metadata?.MiniMax?.Speed ?? 1.0;
        speed = Math.Clamp(speed, 0.5, 2.0); // :contentReference[oaicite:3]{index=3}

        var vol = metadata?.MiniMax?.Vol ?? 1.0;
        if (vol <= 0) vol = 1.0;
        if (vol > 10) vol = 10; // :contentReference[oaicite:4]{index=4}

        var pitch = metadata?.MiniMax?.Pitch ?? 0;
        if (pitch < -12) pitch = -12;
        if (pitch > 12) pitch = 12; // :contentReference[oaicite:5]{index=5}

        // audio_setting
        var format = (request.OutputFormat ?? metadata?.MiniMax?.Format ?? "mp3").ToLowerInvariant();
        format = format is "mp3" or "wav" or "flac" or "pcm" ? format : "mp3"; // :contentReference[oaicite:6]{index=6}

        // strongly recommend URL output (easy + avoids hex decode)
        var outputFormat = "url"; // :contentReference[oaicite:7]{index=7}

        var payload = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["voice_setting"] = new Dictionary<string, object?>
            {
                ["speed"] = speed,
                ["vol"] = vol,
                ["pitch"] = pitch,
                ["voice_id"] = voiceId,
                ["emotion"] = metadata?.MiniMax?.Emotion, // optional :contentReference[oaicite:8]{index=8}
                //     ["text_normalization"] = metadata?.TextNormalization ?? false
            },
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                //       ["sample_rate"] = metadata?.SampleRate, // optional :contentReference[oaicite:9]{index=9}
                //      ["bitrate"] = metadata?.Bitrate,       // optional (mp3 only) :contentReference[oaicite:10]{index=10}
                //     ["channel"] = metadata?.Channel        // optional :contentReference[oaicite:11]{index=11}
            },
            ["stream"] = false,                 // we implement sync non-streaming
            ["output_format"] = outputFormat,   // url or hex :contentReference[oaicite:12]{index=12}
                                                //     ["language_boost"] = metadata?.LanguageBoost
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        var url = $"https://api.novita.ai/v3/{modelPath}"; // e.g. /v3/minimax-speech-02-turbo :contentReference[oaicite:13]{index=13}
        using var resp = await _client.PostAsync(url, content, cancellationToken);
        var respBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(respBytes);
            throw new InvalidOperationException($"MiniMax TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var respJson = Encoding.UTF8.GetString(respBytes);

        // Response: { "audio": "<string>" } :contentReference[oaicite:14]{index=14}
        var audioField = ReadJsonString(respJson, "audio")
            ?? throw new InvalidOperationException($"MiniMax TTS: missing audio field: {respJson}");

        byte[] audioBytes;
        string mime;

        audioBytes = await DownloadPresignedAsync(audioField, cancellationToken);

        mime = format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            _ => "application/octet-stream" // pcm etc
        };

        var base64 = Convert.ToBase64String(audioBytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = format ?? "mp3"
            },
            Warnings = [],
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model!,
                Body = respJson
            }
        };
    }

    private static string? ReadJsonString(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return root.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static bool IsMiniMaxSpeechModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        // support both forms:
        // - minimax-speech-02-turbo (novita endpoint suffix)
        // - speech-02-turbo (friendly alias)
        return model.StartsWith("minimax-speech-", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("speech-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFishSpeechModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        // support both forms:
        // - minimax-speech-02-turbo (novita endpoint suffix)
        // - speech-02-turbo (friendly alias)
        return model.StartsWith("s1", StringComparison.OrdinalIgnoreCase);
    }

}
