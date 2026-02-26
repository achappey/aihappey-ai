using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private static readonly JsonSerializerOptions SmallestAiSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string LightningV31StreamPath = "api/v1/lightning-v3.1/stream";
    private const string LightningV2Path = "api/v1/lightning-v2/get_speech";

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
        var metadata = request.GetProviderMetadata<SmallestAISpeechProviderMetadata>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var (modelId, voiceId) = ParseTtsModelAndVoiceFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var speed = request.Speed ?? 1f;
        if (speed < 0.5f || speed > 2f)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "SmallestAI speed must be between 0.5 and 2.");

        var language = !string.IsNullOrWhiteSpace(request.Language)
            ? request.Language!.Trim()
            : metadata?.Language?.Trim() ?? "auto";

        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat);

        if (string.Equals(modelId, LightningV31Model, StringComparison.OrdinalIgnoreCase))
            return await LightningV31SseSpeechAsync(request, metadata, warnings, now, voiceId, speed, language, outputFormat, cancellationToken);

        if (string.Equals(modelId, LightningV2Model, StringComparison.OrdinalIgnoreCase))
            return await LightningV2SpeechAsync(request, metadata, warnings, now, voiceId, speed, language, outputFormat, cancellationToken);

        throw new NotSupportedException($"{ProviderName} model '{request.Model}' is not supported. Expected '{TtsModelPrefix}[model]/[voiceId]'.");
    }

    private async Task<SpeechResponse> LightningV31SseSpeechAsync(
        SpeechRequest request,
        SmallestAISpeechProviderMetadata? metadata,
        List<object> warnings,
        DateTime now,
        string voiceId,
        float speed,
        string language,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var sampleRate = metadata?.SampleRate ?? 44100;
        if (sampleRate is not (8000 or 16000 or 24000 or 44100))
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.SampleRate), "SmallestAI lightning-v3.1 sampleRate must be one of: 8000, 16000, 24000, 44100.");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_id"] = voiceId,
            ["sample_rate"] = sampleRate,
            ["speed"] = speed,
            ["language"] = language,
            ["output_format"] = outputFormat,
            ["pronunciation_dicts"] = metadata?.PronunciationDicts
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, LightningV31StreamPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SmallestAiSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} lightning-v3.1 stream failed ({(int)resp.StatusCode}): {errBody}");
        }

        var audioBytes = await ReadSseAudioBytesAsync(resp, cancellationToken);
        if (audioBytes.Length == 0)
            throw new InvalidOperationException($"{ProviderName} lightning-v3.1 stream returned no audio chunks.");

        var mime = ResolveMimeType(outputFormat, sampleRate);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = LightningV31Model,
                    voiceId,
                    sampleRate,
                    speed,
                    language,
                    outputFormat,
                    chunksCollected = true
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(new
                {
                    model = LightningV31Model,
                    voiceId,
                    sampleRate,
                    speed,
                    language,
                    outputFormat,
                    bytes = audioBytes.Length
                })
            }
        };
    }

    private async Task<SpeechResponse> LightningV2SpeechAsync(
        SpeechRequest request,
        SmallestAISpeechProviderMetadata? metadata,
        List<object> warnings,
        DateTime now,
        string voiceId,
        float speed,
        string language,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        var sampleRate = metadata?.SampleRate ?? 24000;
        if (sampleRate < 8000 || sampleRate > 24000)
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.SampleRate), "SmallestAI lightning-v2 sampleRate must be between 8000 and 24000.");

        var consistency = metadata?.Consistency ?? 0.5f;
        var similarity = metadata?.Similarity ?? 0f;
        var enhancement = metadata?.Enhancement ?? 1f;

        if (consistency is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.Consistency), "SmallestAI consistency must be between 0 and 1.");
        if (similarity is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.Similarity), "SmallestAI similarity must be between 0 and 1.");
        if (enhancement is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.Enhancement), "SmallestAI enhancement must be between 0 and 2.");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_id"] = voiceId,
            ["sample_rate"] = sampleRate,
            ["speed"] = speed,
            ["consistency"] = consistency,
            ["similarity"] = similarity,
            ["enhancement"] = enhancement,
            ["language"] = language,
            ["output_format"] = outputFormat,
            ["pronunciation_dicts"] = metadata?.PronunciationDicts
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, LightningV2Path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SmallestAiSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} lightning-v2 speech failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = resp.Content.Headers.ContentType?.MediaType ?? ResolveMimeType(outputFormat, sampleRate);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = LightningV2Model,
                    voiceId,
                    sampleRate,
                    speed,
                    language,
                    outputFormat,
                    consistency,
                    similarity,
                    enhancement
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static (string ModelId, string VoiceId) ParseTtsModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(TtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{TtsModelPrefix}[model]/[voiceId]'.");

        var tail = model[TtsModelPrefix.Length..].Trim();
        var slashIndex = tail.LastIndexOf('/');

        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both model id and voice id after 'smallestai/'.", nameof(model));

        var modelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both model id and voice id after 'smallestai/'.", nameof(model));

        return (modelId, voiceId);
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "wave" => "wav",
            "mpeg" => "mp3",
            "ulaw" => "mulaw",
            null or "" => "wav",
            _ => normalized
        };
    }

    private static async Task<byte[]> ReadSseAudioBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        using var ms = new MemoryStream();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line[5..].Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (string.Equals(raw, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    break;

                if (TryParseSseDataEnvelope(raw, out var innerLines))
                {
                    AppendInnerDataLines(ms, innerLines);
                    continue;
                }

                TryAppendBase64Chunk(ms, raw);
            }
        }

        return ms.ToArray();
    }

    private static bool TryParseSseDataEnvelope(string raw, out string[] innerLines)
    {
        innerLines = [];
        if (!(raw.StartsWith("{", StringComparison.Ordinal) && raw.EndsWith("}", StringComparison.Ordinal)))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!TryGetPropertyIgnoreCase(doc.RootElement, "data", out var dataEl)
                || dataEl.ValueKind != JsonValueKind.String)
                return false;

            var data = dataEl.GetString();
            if (string.IsNullOrWhiteSpace(data))
                return false;

            innerLines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendInnerDataLines(Stream target, IEnumerable<string> innerLines)
    {
        foreach (var raw in innerLines)
        {
            if (!raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = raw[5..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            TryAppendBase64Chunk(target, data);
        }
    }

    private static void TryAppendBase64Chunk(Stream target, string data)
    {
        var clean = data.Trim();
        if (clean.StartsWith("\"", StringComparison.Ordinal) && clean.EndsWith("\"", StringComparison.Ordinal) && clean.Length > 1)
            clean = clean[1..^1];

        var commaIndex = clean.IndexOf(",", StringComparison.Ordinal);
        if (clean.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex > 0)
            clean = clean[(commaIndex + 1)..];

        try
        {
            var bytes = Convert.FromBase64String(clean);
            target.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // Ignore non-audio chunks and keep collecting.
        }
    }

    private static string ResolveMimeType(string outputFormat, int sampleRate)
        => outputFormat switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "mulaw" => "audio/basic",
            "pcm" => $"audio/L16;rate={sampleRate}",
            _ => "audio/wav"
        };
}

