using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.CaseDev;

public partial class CaseDevProvider
{
    private const string CaseDevDefaultVoiceId = "EXAVITQu4vr4xnSDxMaL";
    private const string CaseDevDefaultSpeechModelId = "eleven_multilingual_v2";

    private static readonly JsonSerializerOptions CaseDevSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var warnings = new List<object>();
        var prepared = PrepareCaseDevSpeechRequest(request, streaming: false, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/voice/v1/speak")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(prepared.Payload, CaseDevSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveCaseDevSpeechMimeType(prepared.OutputFormat),
                Format = prepared.OutputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = prepared.Payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Input is required.", nameof(options));

        ApplyAuthHeader();
        var prepared = PrepareCaseDevSpeechRequest(request, streaming: true, []);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/voice/v1/speak/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(prepared.Payload, CaseDevSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"CaseDev streaming speech request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[32 * 1024];
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

        yield return new AudioSpeechStreamDone();
    }

    private CaseDevPreparedSpeechRequest PrepareCaseDevSpeechRequest(
        SpeechRequest request,
        bool streaming,
        List<object> warnings)
    {
        var payload = CopyCaseDevOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        var (modelId, modelVoiceId) = ParseCaseDevSpeechModel(request.Model);
        ValidateCaseDevSpeechModel(modelId, streaming);

        var requestedVoice = request.Voice?.Trim();
        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(requestedVoice)
            && !string.Equals(modelVoiceId, requestedVoice, StringComparison.Ordinal))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var metadataOutputFormat = ReadCaseDevString(payload, "output_format");
        var outputFormat = ResolveCaseDevOutputFormat(request.OutputFormat ?? metadataOutputFormat, streaming);

        // Gateway contract fields and model slugs take precedence over raw provider metadata.
        payload["text"] = request.Text;
        payload["model_id"] = modelId;
        payload["voice_id"] = modelVoiceId ?? requestedVoice ?? ReadCaseDevString(payload, "voice_id") ?? CaseDevDefaultVoiceId;
        payload["output_format"] = outputFormat;
        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language_code"] = request.Language.Trim();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        return new CaseDevPreparedSpeechRequest(payload, outputFormat);
    }

    private static Dictionary<string, object?> CopyCaseDevOptions(JsonElement metadata)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in metadata.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static string? ReadCaseDevString(Dictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
            _ => null
        } : null;

    private (string ModelId, string? VoiceId) ParseCaseDevSpeechModel(string model)
    {
        var normalized = NormalizeCaseDevModel(model);
        var separator = normalized.IndexOf('/');
        if (separator < 0)
            return (normalized, null);

        var modelId = normalized[..separator].Trim();
        var voiceId = normalized[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("CaseDev speech model must use the form '{model_id}' or '{model_id}/{voice_id}'.", nameof(model));

        return (modelId, voiceId);
    }

    private static void ValidateCaseDevSpeechModel(string modelId, bool streaming)
    {
        if (!CaseDevSpeechModelIds.Contains(modelId, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported CaseDev speech model '{modelId}'.", nameof(modelId));
        if (!streaming && string.Equals(modelId, "eleven_multilingual_v1", StringComparison.Ordinal))
            throw new ArgumentException("CaseDev model 'eleven_multilingual_v1' is supported only for streaming speech.", nameof(modelId));
    }

    private static string ResolveCaseDevOutputFormat(string? outputFormat, bool streaming)
    {
        var normalized = string.IsNullOrWhiteSpace(outputFormat)
            ? "mp3_44100_128"
            : outputFormat.Trim().ToLowerInvariant() switch
            {
                "mp3" => "mp3_44100_128",
                "pcm" => "pcm_24000",
                var value => value
            };
        var allowed = streaming ? CaseDevStreamingOutputFormats : CaseDevNonStreamingOutputFormats;
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported CaseDev {(streaming ? "streaming " : string.Empty)}speech output format '{normalized}'.", nameof(outputFormat));

        return normalized;
    }

    private static string ResolveCaseDevSpeechMimeType(string outputFormat)
        => outputFormat.Trim().ToLowerInvariant() switch
        {
            var format when format.StartsWith("mp3", StringComparison.Ordinal) => "audio/mpeg",
            var format when format.StartsWith("pcm", StringComparison.Ordinal) => "audio/pcm",
            _ => "application/octet-stream"
        };

    private sealed record CaseDevPreparedSpeechRequest(
        Dictionary<string, object?> Payload,
        string OutputFormat);

}
