using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Noiz;

public partial class NoizProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        ValidateKnownMetadataTypes(metadata);
        var (baseModelId, modelVoiceId) = ParseModelAndVoice(request.Model);

        if (string.Equals(baseModelId, SoundModel, StringComparison.OrdinalIgnoreCase) && modelVoiceId is null)
            return await GenerateSoundAsync(request, cancellationToken);
        if (string.Equals(baseModelId, GuestSpeechModel, StringComparison.OrdinalIgnoreCase) && modelVoiceId is null)
            return await GenerateGuestSpeechAsync(request, cancellationToken);

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Noiz speech model '{request.Model}' is not supported.");

        var metadataVoiceId = ReadMetadataValue<string>(metadata, "voice_id");
        var voiceId = (modelVoiceId ?? request.Voice ?? metadataVoiceId)?.Trim();

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadataVoiceId)
                && !string.Equals(metadataVoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.noiz.voice_id", reason = "voice is derived from model id" });
            }
        }

        var fields = GetProviderOptionFields(metadata,
            "voice_id", "quality_preset", "output_format", "speed", "duration", "target_lang",
            "similarity_enh", "emo", "trim_silence", "save_voice");
        SetFormField(fields, "text", request.Text);
        SetFormField(fields, "voice_id", voiceId);
        SetFormField(fields, "quality_preset", ReadMetadataValue<int?>(metadata, "quality_preset"));
        SetFormField(fields, "output_format", NormalizeOutputFormat(request.OutputFormat ?? ReadMetadataValue<string>(metadata, "output_format")));
        SetFormField(fields, "speed", request.Speed ?? ReadMetadataValue<float?>(metadata, "speed"));
        SetFormField(fields, "duration", ReadMetadataValue<float?>(metadata, "duration"));
        SetFormField(fields, "target_lang", request.Language ?? ReadMetadataValue<string>(metadata, "target_lang"));
        SetFormField(fields, "similarity_enh", ReadMetadataValue<bool?>(metadata, "similarity_enh"));
        SetFormField(fields, "emo", ReadMetadataValue<string>(metadata, "emo"));
        SetFormField(fields, "trim_silence", ReadMetadataValue<bool?>(metadata, "trim_silence"));
        SetFormField(fields, "save_voice", ReadMetadataValue<bool?>(metadata, "save_voice"));
        using var form = CreateForm(fields);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-speech")
        {
            Content = form
        };
        ApplyAuthHeader(httpRequest);

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Noiz TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        var format = NormalizeOutputFormat(request.OutputFormat ?? ReadMetadataValue<string>(metadata, "output_format")) ?? GuessFormat(mediaType) ?? "wav";
        var mimeType = string.IsNullOrWhiteSpace(mediaType) ? GuessMimeType(format) : mediaType!;

        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<SpeechResponse> GenerateSoundAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        if (request.Text.Length > 500)
            throw new ArgumentException("Noiz sound prompt is limited to 500 characters.", nameof(request));

        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var duration = ReadFloat(options, "duration");
        if (duration is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(request), "Noiz sound duration must be between 1 and 30 seconds.");
        var format = ValidateAudioFormat(request.OutputFormat ?? ReadString(options, "output_format"));

        var fields = GetProviderOptionFields(options, "duration", "output_format");
        SetFormField(fields, "prompt", request.Text);
        SetFormField(fields, "duration", duration);
        SetFormField(fields, "output_format", format);
        using var form = CreateForm(fields);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-sound") { Content = form };
        ApplyAuthHeader(httpRequest);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Noiz sound generation failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(bytes)}");

        // The documented binary endpoint does not include a generation ID. Delete only when it explicitly supplies one.
        var generationId = GetHeader(response, "X-Gen-Product-Id");
        if (!string.IsNullOrWhiteSpace(generationId))
            await DeleteSoundHistoryAsync(generationId, cancellationToken);

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Voice)) warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.Speed is not null) warnings.Add(new { type = "unsupported", feature = "speed" });
        return BuildAudioResponse(request, response, bytes, format, warnings);
    }

    private async Task DeleteSoundHistoryAsync(string generationId, CancellationToken cancellationToken)
    {
        // A cleanup failure must not discard successfully downloaded audio.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "text-to-sound-history/" + Uri.EscapeDataString(generationId));
            ApplyAuthHeader(request);
            using var response = await _client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { }
    }

    private async Task<SpeechResponse> GenerateGuestSpeechAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        if (request.Text.Length > 400)
            throw new ArgumentException("Noiz guest speech text is limited to 400 characters.", nameof(request));

        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var language = request.Language ?? ReadString(options, "target_lang");
        if (language is not null && language is not ("en" or "zh" or "ja"))
            throw new ArgumentException("Noiz guest target language must be en, zh, or ja.", nameof(request));
        var voice = request.Voice ?? ReadString(options, "voice_id");
        var whitelist = language switch
        {
            "en" => new[] { "95814add", "5a68d66b", "a845c7de", "883b6b7c", "0e4ab6ec" },
            "zh" => new[] { "3b9f1e27", "b4775100", "ac09aeb4", "87cb2405", "77e15f2c" },
            "ja" => new[] { "063a4491", "4252b9c8", "578b4be2", "f00e45a1", "a9249ce7" },
            _ => new[] { "95814add", "5a68d66b", "a845c7de", "883b6b7c", "0e4ab6ec", "3b9f1e27", "b4775100", "ac09aeb4", "87cb2405", "77e15f2c", "063a4491", "4252b9c8", "578b4be2", "f00e45a1", "a9249ce7" }
        };
        if (!string.IsNullOrWhiteSpace(voice) && !whitelist.Contains(voice, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Voice is not in the Noiz guest whitelist for the target language.", nameof(request));
        var speed = request.Speed ?? ReadFloat(options, "speed");
        if (speed is < 0.5f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(request), "Noiz guest speed must be between 0.5 and 2.0.");
        var filter = ReadString(options, "filter_type");
        if (filter is not null && filter is not ("telephone" or "radio" or "hall" or "studio"))
            throw new ArgumentException("Unsupported Noiz guest filter_type.", nameof(request));
        var format = ValidateAudioFormat(request.OutputFormat ?? ReadString(options, "output_format"));

        var fields = GetProviderOptionFields(options, "voice_id", "target_lang", "speed", "filter_type", "output_format");
        SetFormField(fields, "text", request.Text);
        SetFormField(fields, "voice_id", voice);
        SetFormField(fields, "target_lang", language);
        SetFormField(fields, "speed", speed);
        SetFormField(fields, "filter_type", filter);
        SetFormField(fields, "output_format", format);
        using var form = CreateForm(fields);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "guest/text-to-speech") { Content = form };
        // No Authorization header, even if another request on this provider has a key.
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Noiz guest TTS failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(bytes)}");

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        return BuildAudioResponse(request, response, bytes, format, warnings);
    }

    private SpeechResponse BuildAudioResponse(SpeechRequest request, HttpResponseMessage response, byte[] bytes, string? requestedFormat, List<object> warnings)
    {
        var mime = response.Content.Headers.ContentType?.MediaType;
        var format = GuessFormat(mime) ?? requestedFormat ?? "wav";
        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(bytes), MimeType = mime ?? GuessMimeType(format), Format = format },
            Warnings = warnings,
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    private static string? ValidateAudioFormat(string? format)
    {
        var normalized = NormalizeOutputFormat(format);
        if (normalized is not null && normalized is not ("wav" or "mp3"))
            throw new ArgumentException("Noiz audio output format must be wav or mp3.", nameof(format));
        return normalized;
    }

    private (string BaseModelId, string? VoiceId) ParseModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var slashIndex = raw.IndexOf('/');
        if (slashIndex < 0)
            return (raw, null);

        if (slashIndex == 0 || slashIndex >= raw.Length - 1)
            throw new ArgumentException("Noiz speech model must include both base model id and voice id in the form 'text-to-speech/{voiceId}'.", nameof(model));

        var baseModelId = raw[..slashIndex].Trim();
        var voiceId = raw[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Noiz speech model must include both base model id and voice id in the form 'text-to-speech/{voiceId}'.", nameof(model));

        return (baseModelId, voiceId);
    }

    private static Dictionary<string, string> GetProviderOptionFields(JsonElement options, params string[] mappedProperties)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.ValueKind != JsonValueKind.Object)
            return fields;

        var mapped = new HashSet<string>(mappedProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
        {
            if (!mapped.Contains(property.Name))
                fields[property.Name] = ToFormValue(property.Value);
        }

        return fields;
    }

    private static string ToFormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };

    private static T? ReadMetadataValue<T>(JsonElement options, string propertyName)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return default;

        JsonElement value = default;
        var found = false;
        foreach (var property in options.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        if (!found || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static void ValidateKnownMetadataTypes(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Noiz provider options must be a JSON object.");

        _ = ReadMetadataValue<string>(metadata, "voice_id");
        _ = ReadMetadataValue<int?>(metadata, "quality_preset");
        _ = ReadMetadataValue<string>(metadata, "output_format");
        _ = ReadMetadataValue<float?>(metadata, "speed");
        _ = ReadMetadataValue<float?>(metadata, "duration");
        _ = ReadMetadataValue<string>(metadata, "target_lang");
        _ = ReadMetadataValue<bool?>(metadata, "similarity_enh");
        _ = ReadMetadataValue<string>(metadata, "emo");
        _ = ReadMetadataValue<bool?>(metadata, "trim_silence");
        _ = ReadMetadataValue<bool?>(metadata, "save_voice");
    }

    private static void SetFormField(Dictionary<string, string> fields, string name, object? value)
    {
        if (value is null)
        {
            fields.Remove(name);
            return;
        }

        var stringValue = value switch
        {
            JsonElement element => ToFormValue(element),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(stringValue))
            fields.Remove(name);
        else
            fields[name] = stringValue;
    }

    private static MultipartFormDataContent CreateForm(Dictionary<string, string> fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);

        return form;
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            var fmt => fmt
        };
    }

    private static string? GuessFormat(string? mediaType)
        => mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/mp3" => "mp3",
            "audio/wav" => "wav",
            "audio/x-wav" => "wav",
            _ => null
        };

    private static string GuessMimeType(string format)
        => string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
