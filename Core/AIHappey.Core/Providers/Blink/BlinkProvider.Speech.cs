using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    private static readonly string[] BlinkSpeechModels = ["tts-1", "tts-1-hd"];
    private static readonly string[] BlinkSpeechVoices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"];

    private async Task<SpeechResponse> SpeechRequestBlink(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var warnings = new List<object>();

        var (modelId, modelVoice) = ParseSpeechModelAndVoice(request.Model);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text", "input", "model", "voice", "format", "output_format", "speed"
        };

        var payload = new Dictionary<string, object?>();
        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        // Reserve canonical mapping precedence.
        payload["text"] = request.Text;
        payload["model"] = modelId;

        if (!string.IsNullOrWhiteSpace(modelVoice))
        {
            if (!BlinkSpeechVoices.Any(v => string.Equals(v, modelVoice, StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"Blink speech voice '{modelVoice}' is not supported.");

            payload["voice"] = modelVoice;

            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Voice))
        {
            if (!BlinkSpeechVoices.Any(v => string.Equals(v, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"Blink speech voice '{request.Voice}' is not supported.");

            payload["voice"] = request.Voice.Trim();
        }

        var format = NormalizeBlinkSpeechFormat(request.OutputFormat);
        if (!string.IsNullOrWhiteSpace(format))
            payload["format"] = format;

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var json = JsonSerializer.Serialize(payload, BlinkMediaJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ai/speech")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Blink speech request failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var base64 = ReadString(root, "audio_base64") ?? ReadString(root, "audio");
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Blink speech response did not contain 'audio_base64'.");

        var resolvedFormat = (ReadString(root, "format") ?? format ?? "mp3").Trim().ToLowerInvariant();
        var mimeType = ResolveBlinkSpeechMimeType(resolvedFormat);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = startedAt,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static (string ModelId, string? Voice) ParseSpeechModelAndVoice(string? model)
    {
        var localModel = string.IsNullOrWhiteSpace(model) ? BlinkSpeechModels[0] : model.Trim();

        if (localModel.StartsWith("blink/", StringComparison.OrdinalIgnoreCase))
            localModel = localModel["blink/".Length..];

        if (BlinkSpeechModels.Any(m => string.Equals(m, localModel, StringComparison.OrdinalIgnoreCase)))
            return (localModel.ToLowerInvariant(), null);

        foreach (var baseModel in BlinkSpeechModels)
        {
            var prefix = baseModel + "/";
            if (!localModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var voice = localModel[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(voice))
                throw new ArgumentException("Blink speech model voice shortcut must be in the form 'tts-1/{voice}' or 'tts-1-hd/{voice}'.", nameof(model));

            return (baseModel, voice.ToLowerInvariant());
        }

        throw new NotSupportedException($"Blink speech model '{model}' is not supported. Use 'tts-1', 'tts-1-hd', 'tts-1/{{voice}}', or 'tts-1-hd/{{voice}}'.");
    }

    private static string? NormalizeBlinkSpeechFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var normalized = format.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" or "opus" or "aac" or "flac" or "wav" or "pcm" => normalized,
            _ => normalized
        };
    }

    private static string ResolveBlinkSpeechMimeType(string format)
        => format switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return null;

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}

