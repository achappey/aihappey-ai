using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HeyGen;

public partial class HeyGenProvider
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

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var voiceId = ParseVoiceIdFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var inputType = NormalizeInputType(ReadMetadata<string>(metadata, "inputType")) ?? DetectInputType(request.Text);
        var speed = request.Speed ?? ReadMetadata<float?>(metadata, "speed");
        var language = FirstNonWhiteSpace([request.Language, ReadMetadata<string>(metadata, "language")]);
        var locale = FirstNonWhiteSpace([ReadMetadata<string>(metadata, "locale")]);

        if (speed is { } s && (s < 0.5f || s > 2.0f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "HeyGen speed must be between 0.5 and 2.0.");

        var payload = GetAdditionalMetadata(metadata, "inputType", "speed", "language", "locale");
        payload["text"] = request.Text;
        payload["voice_id"] = voiceId;
        payload["input_type"] = inputType;
        payload["speed"] = speed;
        payload["language"] = language;
        payload["locale"] = locale;

        var ttsJson = await PostJsonAndReadAsync("v3/voices/speech", payload, cancellationToken);

        using var ttsDoc = JsonDocument.Parse(ttsJson);
        EnsureNoHeyGenApiError(ttsDoc.RootElement, ttsJson);

        var data = TryGetPropertyIgnoreCase(ttsDoc.RootElement, "data", out var dataEl)
            ? dataEl
            : ttsDoc.RootElement;

        var audioUrl = ReadString(data, "audio_url")
            ?? ReadString(data, "audioUrl")
            ?? ReadString(data, "url");

        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"{ProviderName} TTS response missing data.audio_url: {ttsJson}");

        using var audioResp = await _client.GetAsync(audioUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var mimeType = ResolveMimeType(audioResp.Content.Headers.ContentType?.MediaType, audioUrl, request.OutputFormat);
        var format = ResolveFormat(mimeType, audioUrl, request.OutputFormat);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = ttsDoc.RootElement.Clone()
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static Dictionary<string, object?> GetAdditionalMetadata(JsonElement metadata, params string[] mappedProperties)
    {
        var result = new Dictionary<string, object?>();
        if (metadata.ValueKind == JsonValueKind.Undefined || metadata.ValueKind == JsonValueKind.Null)
            return result;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("HeyGen provider options must be a JSON object.");

        var mapped = new HashSet<string>(mappedProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
        {
            if (!mapped.Contains(property.Name))
                result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static T? ReadMetadata<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.Deserialize<T>(SpeechJson);
        }

        return default;
    }

    private async Task<string> PostJsonAndReadAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} request '{path}' failed ({(int)resp.StatusCode}): {body}");

        return body;
    }

    private static void EnsureNoHeyGenApiError(JsonElement root, string raw)
    {
        if (!TryGetPropertyIgnoreCase(root, "error", out var errorEl)
            || errorEl.ValueKind == JsonValueKind.Null
            || errorEl.ValueKind == JsonValueKind.Undefined)
            return;

        var code = ReadString(errorEl, "code");
        var message = ReadString(errorEl, "message") ?? "Unknown HeyGen error";
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(code)
            ? $"{ProviderName} API error: {message}. Raw: {raw}"
            : $"{ProviderName} API error ({code}): {message}. Raw: {raw}");
    }

    private static string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(ProviderId.Length + 1)..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Model must be '{ProviderId}/[voiceId]'.", nameof(model));

        return trimmed;
    }

    private static string DetectInputType(string text)
        => text.TrimStart().StartsWith("<speak", StringComparison.OrdinalIgnoreCase)
            ? "ssml"
            : "text";

    private static string? NormalizeInputType(string? inputType)
    {
        if (string.IsNullOrWhiteSpace(inputType))
            return null;

        var normalized = inputType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "text" => "text",
            "ssml" => "ssml",
            _ => throw new ArgumentException("HeyGen inputType must be 'text' or 'ssml'.")
        };
    }

    private static double? ReadNumberAsDouble(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            return d;

        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static string ResolveMimeType(string? contentType, string? audioUrl, string? requestedOutputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested switch
            {
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "opus" => "audio/ogg",
                "flac" => "audio/flac",
                "aac" => "audio/aac",
                _ => "audio/mpeg"
            };

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        return ext switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "mp3" => "audio/mpeg",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveFormat(string mimeType, string? audioUrl, string? requestedOutputFormat)
    {
        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("flac")) return "flac";
        if (mt.Contains("aac")) return "aac";

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ext))
            return ext switch
            {
                "mpeg" => "mp3",
                "wave" => "wav",
                _ => ext
            };

        return "mp3";
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => value
        };
    }
}
