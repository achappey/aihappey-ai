using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    private static readonly JsonSerializerOptions ElevenLabsOpenAISpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        var request = BuildElevenLabsOpenAISpeechRequest(options, streaming: false);
        ApplyAuthHeader();

        using var httpRequest = CreateElevenLabsOpenAISpeechHttpRequest(request);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateElevenLabsOpenAISpeechException(request.Kind, response, bytes);

        if (request.WithTimestamps && request.Kind is ElevenLabsOpenAISpeechKind.TextToSpeech or ElevenLabsOpenAISpeechKind.Dialogue)
            bytes = ReadElevenLabsBase64Audio(bytes, request.Kind);

        return (bytes, ResolveElevenLabsSpeechMimeType(response, request.OutputFormat));
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildElevenLabsOpenAISpeechRequest(options, streaming: true);
        ApplyAuthHeader();

        using var httpRequest = CreateElevenLabsOpenAISpeechHttpRequest(request);
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            throw CreateElevenLabsOpenAISpeechException(request.Kind, response, error);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (request.IsJsonStream)
        {
            await foreach (var audio in ReadElevenLabsJsonAudioStreamAsync(stream, cancellationToken))
                yield return new AudioSpeechStreamDelta { Audio = audio };
        }
        else
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                yield return new AudioSpeechStreamDelta
                {
                    Audio = Convert.ToBase64String(buffer, 0, read)
                };
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDone();
    }

    private ElevenLabsOpenAISpeechRequest BuildElevenLabsOpenAISpeechRequest(AudioSpeechRequest options, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is required.", nameof(options));

        var model = NormalizeElevenLabsOpenAISpeechModel(options.Model);
        var kind = model.EndsWith("/text-to-dialogue", StringComparison.OrdinalIgnoreCase)
            ? ElevenLabsOpenAISpeechKind.Dialogue
            : model is "music_v1" or "music_v2"
                ? ElevenLabsOpenAISpeechKind.Music
                : ElevenLabsOpenAISpeechKind.TextToSpeech;

        if (kind == ElevenLabsOpenAISpeechKind.Dialogue)
            model = model[..^"/text-to-dialogue".Length];

        var withTimestamps = ReadElevenLabsBoolean(options, "with_timestamps") ?? false;
        var detailedMusicStream = kind == ElevenLabsOpenAISpeechKind.Music
            && streaming
            && ((ReadElevenLabsBoolean(options, "detailed_stream") ?? false)
                || withTimestamps
                || (ReadElevenLabsBoolean(options, "with_waveform_visual") ?? false));

        var outputFormat = ResolveElevenLabsOutputFormat(options.ResponseFormat, kind);
        var query = new List<string> { $"output_format={Uri.EscapeDataString(outputFormat)}" };
        AddElevenLabsQueryOption(options, query, "enable_logging");
        if (kind == ElevenLabsOpenAISpeechKind.TextToSpeech)
            AddElevenLabsQueryOption(options, query, "optimize_streaming_latency");

        Dictionary<string, object?> body;
        string path;
        var jsonStream = false;

        switch (kind)
        {
            case ElevenLabsOpenAISpeechKind.TextToSpeech:
            {
                var voice = options.Voice?.Trim();
                if (string.IsNullOrWhiteSpace(voice))
                    throw new ArgumentException("'voice' (ElevenLabs voice_id) is required for text-to-speech.", nameof(options));

                body = BuildElevenLabsTextToSpeechBody(options, model);
                path = $"v1/text-to-speech/{Uri.EscapeDataString(voice)}";
                if (streaming)
                {
                    path += withTimestamps ? "/stream/with-timestamps" : "/stream";
                    jsonStream = withTimestamps;
                }
                else if (withTimestamps)
                {
                    path += "/with-timestamps";
                }

                break;
            }

            case ElevenLabsOpenAISpeechKind.Dialogue:
                body = BuildElevenLabsDialogueBody(options, model);
                path = "v1/text-to-dialogue";
                if (streaming)
                {
                    path += withTimestamps ? "/stream/with-timestamps" : "/stream";
                    jsonStream = withTimestamps;
                }
                else if (withTimestamps)
                {
                    path += "/with-timestamps";
                }

                break;

            case ElevenLabsOpenAISpeechKind.Music:
                body = BuildElevenLabsMusicBody(options, model, detailedMusicStream);
                path = detailedMusicStream
                    ? "v1/music/detailed/stream"
                    : streaming
                        ? "v1/music/stream"
                        : "v1/music";
                jsonStream = detailedMusicStream;
                break;

            default:
                throw new InvalidOperationException("Unsupported ElevenLabs speech request kind.");
        }

        return new ElevenLabsOpenAISpeechRequest(
            kind,
            path + "?" + string.Join("&", query),
            body,
            outputFormat,
            withTimestamps,
            jsonStream);
    }

    private static Dictionary<string, object?> BuildElevenLabsTextToSpeechBody(AudioSpeechRequest options, string model)
    {
        var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = options.Input,
            ["model_id"] = model
        };

        CopyElevenLabsOptions(options, body,
            "language_code",
            "pronunciation_dictionary_locators",
            "seed",
            "previous_text",
            "next_text",
            "previous_request_ids",
            "next_request_ids",
            "apply_text_normalization",
            "apply_language_text_normalization",
            "use_pvc_as_ivc");

        var voiceSettings = ReadElevenLabsObject(options, "voice_settings");
        if (options.Speed is not null)
        {
            if (options.Speed <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.Speed), "ElevenLabs speech speed must be greater than zero.");

            voiceSettings ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            voiceSettings["speed"] = options.Speed.Value;
        }

        if (voiceSettings is not null)
            body["voice_settings"] = voiceSettings;

        return body;
    }

    private static Dictionary<string, object?> BuildElevenLabsDialogueBody(AudioSpeechRequest options, string model)
    {
        var inputs = ReadElevenLabsProperty(options, "inputs");
        if (inputs is null || inputs.Value.ValueKind != JsonValueKind.Array || inputs.Value.GetArrayLength() == 0)
            throw new ArgumentException("ElevenLabs dialogue requires a non-empty extension field 'inputs'.", nameof(options));

        var uniqueVoices = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;
        foreach (var input in inputs.Value.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString())
                || !input.TryGetProperty("voice_id", out var voice)
                || voice.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(voice.GetString()))
            {
                throw new ArgumentException("Every ElevenLabs dialogue input must contain non-empty 'text' and 'voice_id' strings.", nameof(options));
            }

            totalCharacters += text.GetString()!.Length;
            uniqueVoices.Add(voice.GetString()!);
        }

        if (uniqueVoices.Count > 10)
            throw new ArgumentException("ElevenLabs dialogue supports at most 10 unique voice IDs.", nameof(options));
        if (totalCharacters > 2_000)
            throw new ArgumentException("ElevenLabs dialogue supports at most 2,000 total input characters per request.", nameof(options));

        var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["inputs"] = inputs.Value.Clone(),
            ["model_id"] = model
        };

        CopyElevenLabsOptions(options, body,
            "language_code",
            "settings",
            "pronunciation_dictionary_locators",
            "seed",
            "apply_text_normalization");

        return body;
    }

    private static Dictionary<string, object?> BuildElevenLabsMusicBody(
        AudioSpeechRequest options,
        string model,
        bool detailedStream)
    {
        var compositionPlan = ReadElevenLabsProperty(options, "composition_plan");
        var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_id"] = model
        };

        if (compositionPlan is { } plan && plan.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            body["composition_plan"] = plan.Clone();
        else
            body["prompt"] = options.Input;

        CopyElevenLabsOptions(options, body,
            "music_length_ms",
            "seed",
            "force_instrumental",
            "finetune_id",
            "respect_sections_durations",
            "store_for_inpainting",
            "sign_with_c2pa");

        if (detailedStream)
        {
            CopyElevenLabsOptions(options, body, "with_timestamps", "with_waveform_visual");
        }

        return body;
    }

    private static HttpRequestMessage CreateElevenLabsOpenAISpeechHttpRequest(ElevenLabsOpenAISpeechRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, request.RelativeUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request.Body, ElevenLabsOpenAISpeechJson),
                Encoding.UTF8,
                "application/json")
        };

        message.Headers.Accept.ParseAdd(request.IsJsonStream ? "text/event-stream, application/json" : "audio/*");
        return message;
    }

    private static async IAsyncEnumerable<string> ReadElevenLabsJsonAudioStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = line.Trim();
            if (data.Length == 0 || data.StartsWith(':'))
                continue;
            if (data.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
                || data.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || data.StartsWith("retry:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                data = data[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
                continue;

            JsonDocument? document = null;
            string? directAudio = null;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                TryNormalizeElevenLabsBase64(data, out directAudio);
            }

            if (directAudio is not null)
            {
                yield return directAudio;
                continue;
            }

            if (document is null)
                continue;

            using (document)
            {
                foreach (var audio in FindElevenLabsBase64Audio(document.RootElement))
                    yield return audio;
            }
        }
    }

    private static IEnumerable<string> FindElevenLabsBase64Audio(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((property.NameEquals("audio_base64") || property.NameEquals("audio"))
                    && property.Value.ValueKind == JsonValueKind.String
                    && TryNormalizeElevenLabsBase64(property.Value.GetString(), out var audio))
                {
                    yield return audio;
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    foreach (var nested in FindElevenLabsBase64Audio(property.Value))
                        yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in FindElevenLabsBase64Audio(item))
                    yield return nested;
            }
        }
    }

    private static byte[] ReadElevenLabsBase64Audio(byte[] response, ElevenLabsOpenAISpeechKind kind)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("audio_base64", out var audio)
                || audio.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(audio.GetString()))
            {
                throw new InvalidOperationException($"ElevenLabs {GetElevenLabsSpeechOperationName(kind)} timestamp response did not include audio_base64.");
            }

            return Convert.FromBase64String(audio.GetString()!);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ElevenLabs {GetElevenLabsSpeechOperationName(kind)} returned invalid timestamp JSON.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"ElevenLabs {GetElevenLabsSpeechOperationName(kind)} returned invalid base64 audio.", ex);
        }
    }

    private static InvalidOperationException CreateElevenLabsOpenAISpeechException(
        ElevenLabsOpenAISpeechKind kind,
        HttpResponseMessage response,
        byte[] error)
        => new($"ElevenLabs {GetElevenLabsSpeechOperationName(kind)} failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(error)}");

    private static string GetElevenLabsSpeechOperationName(ElevenLabsOpenAISpeechKind kind)
        => kind switch
        {
            ElevenLabsOpenAISpeechKind.TextToSpeech => "text-to-speech",
            ElevenLabsOpenAISpeechKind.Dialogue => "text-to-dialogue",
            ElevenLabsOpenAISpeechKind.Music => "music",
            _ => "speech"
        };

    private static string NormalizeElevenLabsOpenAISpeechModel(string model)
    {
        var normalized = model.Trim();
        const string prefix = "elevenlabs/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];
        return normalized;
    }

    private static string ResolveElevenLabsOutputFormat(string? responseFormat, ElevenLabsOpenAISpeechKind kind)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return kind == ElevenLabsOpenAISpeechKind.Music ? "auto" : "mp3_44100_128";

        var format = responseFormat.Trim().ToLowerInvariant();
        if (format.Contains('_') || format == "auto")
            return format;

        return format switch
        {
            "mp3" => "mp3_44100_128",
            "opus" => "opus_48000_128",
            "wav" => "wav_44100",
            "pcm" => "pcm_44100",
            "ulaw" or "mulaw" => "ulaw_8000",
            "alaw" => "alaw_8000",
            _ => format
        };
    }

    private static string ResolveElevenLabsSpeechMimeType(HttpResponseMessage response, string outputFormat)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType)
            && !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return mediaType;

        var codec = outputFormat.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return codec switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/ogg",
            "wav" => "audio/wav",
            "pcm" => "audio/wav",
            "ulaw" or "alaw" => "audio/basic",
            _ => "application/octet-stream"
        };
    }

    private static void AddElevenLabsQueryOption(AudioSpeechRequest options, ICollection<string> query, string name)
    {
        var value = ReadElevenLabsProperty(options, name);
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        var text = value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.Value.GetRawText()
        };
        if (!string.IsNullOrWhiteSpace(text))
            query.Add($"{name}={Uri.EscapeDataString(text)}");
    }

    private static void CopyElevenLabsOptions(
        AudioSpeechRequest options,
        IDictionary<string, object?> destination,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadElevenLabsProperty(options, name);
            if (value is not null && value.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                destination[name] = value.Value.Clone();
        }
    }

    private static JsonElement? ReadElevenLabsProperty(AudioSpeechRequest options, string name)
    {
        if (options.AdditionalProperties is null)
            return null;

        foreach (var property in options.AdditionalProperties)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static bool? ReadElevenLabsBoolean(AudioSpeechRequest options, string name)
    {
        var value = ReadElevenLabsProperty(options, name);
        if (value is null)
            return null;

        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.Value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static Dictionary<string, object?>? ReadElevenLabsObject(AudioSpeechRequest options, string name)
    {
        var value = ReadElevenLabsProperty(options, name);
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"ElevenLabs extension field '{name}' must be an object.", nameof(options));

        return value.Value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeElevenLabsBase64(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        var comma = candidate.IndexOf(',');
        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            candidate = candidate[(comma + 1)..];

        try
        {
            normalized = Convert.ToBase64String(Convert.FromBase64String(candidate));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private enum ElevenLabsOpenAISpeechKind
    {
        TextToSpeech,
        Dialogue,
        Music
    }

    private sealed record ElevenLabsOpenAISpeechRequest(
        ElevenLabsOpenAISpeechKind Kind,
        string RelativeUrl,
        Dictionary<string, object?> Body,
        string OutputFormat,
        bool WithTimestamps,
        bool IsJsonStream);
}
