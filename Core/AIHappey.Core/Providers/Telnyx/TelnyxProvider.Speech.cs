using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    private const string TelnyxSpeechModel = "text-to-speech";
    private static readonly JsonSerializerOptions TelnyxSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTelnyxSpeechInput(request.Model, request.Text);

        var (voice, voiceFromModel) = ResolveTelnyxVoice(request.Model, request.Voice);
        var payload = GetTelnyxProviderPayload(request.ProviderOptions);
        payload["text"] = request.Text;
        payload["voice"] = voice;
        payload["output_type"] = "binary_output";

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language"] = request.Language;
        if (request.Speed is not null)
        {
            var settings = payload.TryGetValue("voice_settings", out var existing)
                && existing is JsonElement { ValueKind: JsonValueKind.Object } settingsElement
                    ? settingsElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            settings["speed"] = request.Speed;
            payload["voice_settings"] = settings;
        }

        var warnings = new List<object>();
        if (voiceFromModel && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice, voice, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "Telnyx determines the binary audio format" });

        var (audio, mimeType, headers) = await SendTelnyxSpeechAsync(payload, cancellationToken);
        var format = MimeTypeToFormat(mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { voice }, TelnyxSpeechJson)
            },
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = headers
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ValidateTelnyxOpenAISpeechRequest(options);
        var (voice, _) = ResolveTelnyxVoice(options.Model, options.Voice);
        var payload = GetTelnyxAdditionalPayload(options.AdditionalProperties);
        payload["text"] = options.Input;
        payload["voice"] = voice;
        payload["output_type"] = "binary_output";

        if (options.Speed is not null)
        {
            var settings = new Dictionary<string, object?> { ["speed"] = options.Speed };
            payload["voice_settings"] = settings;
        }

        var (audio, mimeType, _) = await SendTelnyxSpeechAsync(payload, cancellationToken);
        return (audio, mimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateTelnyxOpenAISpeechRequest(options);
        var (voice, _) = ResolveTelnyxVoice(options.Model, options.Voice);
        var payload = GetTelnyxAdditionalPayload(options.AdditionalProperties);
        payload["text"] = options.Input;
        payload["voice"] = voice;
        payload["output_type"] = "binary_output";

        if (options.Speed is not null)
            payload["voice_settings"] = new Dictionary<string, object?> { ["speed"] = options.Speed };

        ApplyAuthHeader();
        using var httpRequest = CreateTelnyxSpeechRequest(payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Telnyx TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, string> Headers)> SendTelnyxSpeechAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var httpRequest = CreateTelnyxSpeechRequest(payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(audio);
            throw new InvalidOperationException($"Telnyx TTS failed ({(int)response.StatusCode}): {error}");
        }

        return (
            audio,
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            response.GetHeaders());
    }

    private static HttpRequestMessage CreateTelnyxSpeechRequest(Dictionary<string, object?> payload)
        => new(HttpMethod.Post, "text-to-speech/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TelnyxSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

    private Dictionary<string, object?> GetTelnyxProviderPayload(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue(GetIdentifier(), out var raw)
            || raw.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        return raw.EnumerateObject().ToDictionary(
            p => p.Name,
            p => (object?)p.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> GetTelnyxAdditionalPayload(Dictionary<string, JsonElement>? additionalProperties)
        => additionalProperties?.ToDictionary(
               p => p.Key,
               p => (object?)p.Value.Clone(),
               StringComparer.OrdinalIgnoreCase)
           ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private (string Voice, bool FromModel) ResolveTelnyxVoice(string model, string? explicitVoice)
    {
        var local = NormalizeTelnyxModelId(model).Trim('/');
        var prefix = TelnyxSpeechModel + "/";
        var modelVoice = local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? local[prefix.Length..]
            : null;

        if (!string.Equals(local, TelnyxSpeechModel, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(modelVoice))
            throw new NotSupportedException($"Telnyx speech model '{model}' is not supported.");

        var voice = string.IsNullOrWhiteSpace(modelVoice) ? explicitVoice?.Trim() : modelVoice.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Telnyx requires a voice in the model id or request voice field.", nameof(model));

        return (voice, !string.IsNullOrWhiteSpace(modelVoice));
    }

    private static void ValidateTelnyxSpeechInput(string model, string text)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));
    }

    private static void ValidateTelnyxOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateTelnyxSpeechInput(options.Model, options.Input);
    }

    private static string MimeTypeToFormat(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/mpeg" => "mp3",
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/ogg" => "ogg",
        "audio/flac" => "flac",
        _ => "binary"
    };
}

