using System.Text;
using System.Text.Json;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    private static readonly HashSet<string> StraicoReservedSpeechFields =
    [
        "model",
        "text",
        "voice_id",
        "language-code"
    ];

    private async Task<SpeechResponse> SpeechRequestInternal(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var formFields = new Dictionary<string, string>
        {
            ["model"] = request.Model.Trim(),
            ["text"] = request.Text,
            ["voice_id"] = request.Voice.Trim()
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
            formFields["language-code"] = request.Language.Trim();

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var straicoOptions)
            && straicoOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in straicoOptions.EnumerateObject())
            {
                if (StraicoReservedSpeechFields.Contains(property.Name))
                    continue;

                formFields[property.Name] = ToFormFieldValue(property.Value);
            }
        }

        using var ttsRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts/create")
        {
            Content = new FormUrlEncodedContent(formFields)
        };

        using var ttsResponse = await _client.SendAsync(ttsRequest, cancellationToken);
        var ttsRaw = await ttsResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!ttsResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Straico TTS failed ({(int)ttsResponse.StatusCode}): {ttsRaw}");

        using var ttsDoc = JsonDocument.Parse(ttsRaw);
        var ttsRoot = ttsDoc.RootElement.Clone();

        var audioUrl = TryGetStraicoAudioUrl(ttsRoot);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException("Straico TTS response did not contain data.audio.");

        using var audioResponse = await _client.GetAsync(audioUrl, cancellationToken);
        var audioBytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResponse.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(audioBytes);
            throw new InvalidOperationException($"Straico TTS audio download failed ({(int)audioResponse.StatusCode}): {error}");
        }

        var format = GuessStraicoAudioFormat(audioUrl) ?? "mp3";
        var mime = audioResponse.Content.Headers.ContentType?.MediaType
                   ?? GuessStraicoAudioMimeType(audioUrl)
                   ?? OpenAIProvider.MapToAudioMimeType(format);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    audio = audioUrl,
                    raw = ttsRoot.Clone()
                })
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = ttsRoot.Clone()
            }
        };
    }

    private static string? TryGetStraicoAudioUrl(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("audio", out var audio)
            && audio.ValueKind == JsonValueKind.String)
        {
            return audio.GetString();
        }

        return null;
    }

    private static string ToFormFieldValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static string? GuessStraicoAudioFormat(string? audioUrl)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
            return null;

        var url = audioUrl.Trim();

        if (url.Contains(".wav", StringComparison.OrdinalIgnoreCase))
            return "wav";
        if (url.Contains(".ogg", StringComparison.OrdinalIgnoreCase))
            return "ogg";
        if (url.Contains(".flac", StringComparison.OrdinalIgnoreCase))
            return "flac";
        if (url.Contains(".aac", StringComparison.OrdinalIgnoreCase))
            return "aac";
        if (url.Contains(".opus", StringComparison.OrdinalIgnoreCase))
            return "opus";
        if (url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
            return "mp3";

        return null;
    }

    private static string? GuessStraicoAudioMimeType(string? audioUrl)
    {
        var format = GuessStraicoAudioFormat(audioUrl);
        return OpenAIProvider.MapToAudioMimeType(format);
    }
}
