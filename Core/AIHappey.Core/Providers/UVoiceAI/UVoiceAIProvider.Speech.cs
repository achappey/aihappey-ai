using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.UVoiceAI;

public partial class UVoiceAIProvider
{
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

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Text.Trim().Length < 5)
            throw new ArgumentException("Text must be at least 5 characters.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var outputFormatOption = ReadStringOption(metadata, "outputFormat");
        var outputTypeOption = ReadStringOption(metadata, "outputType");
        var autoBreakOption = ReadBooleanOption(metadata, "autoBreak");
        var volumeOption = ReadSingleOption(metadata, "volume");
        var pitchOption = ReadSingleOption(metadata, "pitch");
        var keyOption = ReadInt32Option(metadata, "key");

        var outputFormat = NormalizeFormat(request.OutputFormat ?? outputFormatOption);
        if (outputFormat is not ("wav" or "mp3"))
            throw new ArgumentException("outputFormat must be 'wav' or 'mp3'.", nameof(request));

        var outputType = NormalizeOutputType(outputTypeOption);
        var autoBreak = autoBreakOption;
        var speed = request.Speed;
        var volume = volumeOption;
        var pitch = pitchOption;
        var key = keyOption;

        if (speed is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "UVoiceAI speed must be between 0.5 and 1.5.");
        if (volume is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException("Volume", "UVoiceAI volume must be between 0.5 and 1.5.");
        if (pitch is < 0.5f or > 1.5f)
            throw new ArgumentOutOfRangeException("Pitch", "UVoiceAI pitch must be between 0.5 and 1.5.");
        if (key is < -6 or > 6)
            throw new ArgumentOutOfRangeException("Key", "UVoiceAI key must be between -6 and 6.");

        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyAdditionalOptions(metadata, settings);
        SetSetting(settings, "voiceID", voiceId);
        SetSetting(settings, "text", request.Text);
        SetSetting(settings, "outputFormat", outputFormat);
        SetSetting(settings, "outputType", outputType);

        if (autoBreak is not null)
            SetSetting(settings, "autoBreak", autoBreak.Value);
        else
            RemoveSetting(settings, "autoBreak");

        if (speed is not null)
            SetSetting(settings, "speed", speed.Value);

        if (volume is not null)
            SetSetting(settings, "volume", volume.Value);
        else
            RemoveSetting(settings, "volume");

        if (pitch is not null)
            SetSetting(settings, "pitch", pitch.Value);
        else
            RemoveSetting(settings, "pitch");

        if (key is not null)
            SetSetting(settings, "key", key.Value);
        else
            RemoveSetting(settings, "key");

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["settings"] = settings
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        string? audioUrl = null;
        byte[] audioBytes = bytes;
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        JsonElement? body = null;
        if (string.Equals(outputType, "url", StringComparison.OrdinalIgnoreCase)
            || IsLikelyJson(contentType, bytes))
        {
            using var doc = JsonDocument.Parse(bytes);
            audioUrl = ReadAudioUrl(doc.RootElement)
                ?? throw new InvalidOperationException($"{ProviderName} returned URL output without an audio URL.");

            using var audioResp = await _client.GetAsync(audioUrl, cancellationToken);
            var audioUrlBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!audioResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} audio URL fetch failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioUrlBytes)}");

            audioBytes = audioUrlBytes;
            contentType = audioResp.Content.Headers.ContentType?.MediaType;
            body = doc.RootElement.Clone();
        }

        var mimeType = ResolveSpeechMimeType(contentType, outputFormat);
        var responseFormat = ResolveSpeechFormat(outputFormat, mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload,
            },
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                Body = body,
                ModelId = request.Model.ToModelId(GetIdentifier()),
            }
        };
    }

    private static string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith("uvoiceai/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Split('/', 2)[1];

        if (trimmed.StartsWith(UVoiceModelPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[UVoiceModelPrefix.Length..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Model must contain a voice id.", nameof(model));

        return trimmed;
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "wav";

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => value
        };
    }

    private static string NormalizeOutputType(string? outputType)
    {
        if (string.IsNullOrWhiteSpace(outputType))
            return "binary";

        var value = outputType.Trim().ToLowerInvariant();
        return value == "url" ? "url" : "binary";
    }

    private static void CopyAdditionalOptions(JsonElement metadata, Dictionary<string, object?> settings)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return;

        EnsureMetadataObject(metadata);
        foreach (var property in metadata.EnumerateObject())
            settings[property.Name] = property.Value.Clone();
    }

    private static string? ReadStringOption(JsonElement metadata, string name)
        => ReadMappedReferenceOption<string>(metadata, name, value => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException($"The UVoiceAI provider option '{name}' must be a string or null.")
        });

    private static bool? ReadBooleanOption(JsonElement metadata, string name)
        => ReadMappedValueOption<bool>(metadata, name, value => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"The UVoiceAI provider option '{name}' must be a boolean or null.")
        });

    private static float? ReadSingleOption(JsonElement metadata, string name)
        => ReadMappedValueOption<float>(metadata, name, value =>
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number))
                throw new JsonException($"The UVoiceAI provider option '{name}' must be a number or null.");

            return number;
        });

    private static int? ReadInt32Option(JsonElement metadata, string name)
        => ReadMappedValueOption<int>(metadata, name, value =>
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                throw new JsonException($"The UVoiceAI provider option '{name}' must be an integer or null.");

            return number;
        });

    private static T? ReadMappedReferenceOption<T>(JsonElement metadata, string name, Func<JsonElement, T?> read)
        where T : class
    {
        var value = FindMappedOption(metadata, name);
        return value is { } found ? read(found) : default;
    }

    private static T? ReadMappedValueOption<T>(JsonElement metadata, string name, Func<JsonElement, T?> read)
        where T : struct
    {
        var value = FindMappedOption(metadata, name);
        return value is { } found ? read(found) : default;
    }

    private static JsonElement? FindMappedOption(JsonElement metadata, string name)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return null;

        EnsureMetadataObject(metadata);
        JsonElement? found = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }

        return found;
    }

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("UVoiceAI providerOptions must be a JSON object.");
    }

    private static void SetSetting(Dictionary<string, object?> settings, string name, object value)
    {
        RemoveSetting(settings, name);
        settings[name] = value;
    }

    private static void RemoveSetting(Dictionary<string, object?> settings, string name)
    {
        foreach (var key in settings.Keys.Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
            settings.Remove(key);
    }

    private static bool IsLikelyJson(string? contentType, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = Encoding.UTF8.GetString([.. bytes.Take(64)]).TrimStart();
        return prefix.StartsWith("{", StringComparison.Ordinal) || prefix.StartsWith("[", StringComparison.Ordinal);
    }

    private static string? ReadAudioUrl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
            return root.GetString();

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var keys = new[] { "url", "audioUrl", "audio_url", "file", "fileUrl", "file_url", "result" };
        foreach (var key in keys)
        {
            if (!TryGetPropertyIgnoreCase(root, key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var url = value.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return null;
    }

    private static string ResolveSpeechMimeType(string? contentType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return outputFormat == "wav" ? "audio/wav" : "audio/mpeg";
    }

    private static string ResolveSpeechFormat(string outputFormat, string mimeType)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
            return outputFormat;

        if (mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase))
            return "wav";

        return "mp3";
    }

    private static string? TryReadHeader(HttpResponseMessage response, string headerName)
        => response.Headers.TryGetValues(headerName, out var values) ? values.FirstOrDefault() : null;
}
