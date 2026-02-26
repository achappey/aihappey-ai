using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.UnrealSpeech;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.UnrealSpeech;

public partial class UnrealSpeechProvider
{
    private static readonly JsonSerializerOptions UnrealSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> AllowedBitrates =
    [
        "16k", "32k", "48k", "64k", "128k", "192k", "256k", "320k"
    ];

    private static readonly HashSet<string> AllowedTimestampTypes =
    [
        "word", "sentence"
    ];

    private static readonly HashSet<string> AllowedCodecs =
    [
        "libmp3lame", "pcm_mulaw", "pcm_s16le"
    ];

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
        var metadata = request.GetProviderMetadata<UnrealSpeechSpeechProviderMetadata>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        var voiceId = request.Model;
        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var bitrate = (metadata?.Bitrate ?? "192k").Trim().ToLowerInvariant();
        if (!AllowedBitrates.Contains(bitrate))
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.Bitrate), "UnrealSpeech bitrate must be one of: 16k, 32k, 48k, 64k, 128k, 192k, 256k, 320k.");

        var speed = metadata?.Speed ?? request.Speed ?? 0f;
        if (speed < -1f || speed > 1f)
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.Speed), "UnrealSpeech speed must be between -1.0 and 1.0.");

        var pitch = metadata?.Pitch ?? 1f;
        if (pitch < 0.5f || pitch > 1.5f)
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.Pitch), "UnrealSpeech pitch must be between 0.5 and 1.5.");

        var timestampType = string.IsNullOrWhiteSpace(metadata?.TimestampType)
            ? "sentence"
            : metadata!.TimestampType!.Trim().ToLowerInvariant();
        if (!AllowedTimestampTypes.Contains(timestampType))
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.TimestampType), "UnrealSpeech timestampType must be 'word' or 'sentence'.");

        var codec = metadata?.Codec?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(codec) && !AllowedCodecs.Contains(codec))
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.Codec), "UnrealSpeech codec must be one of: libmp3lame, pcm_mulaw, pcm_s16le.");

        var temperature = metadata?.Temperature;
        if (temperature is not null && (temperature < 0.1f || temperature > 0.8f))
            throw new ArgumentOutOfRangeException(nameof(UnrealSpeechSpeechProviderMetadata.Temperature), "UnrealSpeech temperature must be between 0.1 and 0.8.");

        var text = request.Text;
        var charCount = text.Length;
        var endpoint = charCount <= 1000
            ? "stream"
            : charCount <= 3000
                ? "speech"
                : "synthesisTasks";

        byte[] audioBytes;
        object? apiResult;

        if (endpoint == "stream")
        {
            var payload = new Dictionary<string, object?>
            {
                ["Text"] = text,
                ["VoiceId"] = voiceId,
                ["Bitrate"] = bitrate,
                ["Speed"] = speed,
                ["Pitch"] = pitch,
                ["Codec"] = codec,
                ["Temperature"] = temperature
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "stream")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, UnrealSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            audioBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} stream failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

            apiResult = new { endpoint, streamBytes = audioBytes.Length };
        }
        else if (endpoint == "speech")
        {
            if (!string.IsNullOrWhiteSpace(codec))
                warnings.Add(new { type = "ignored", feature = "codec", reason = "codec is supported only for /stream" });
            if (temperature is not null)
                warnings.Add(new { type = "ignored", feature = "temperature", reason = "temperature is supported only for /stream" });

            var payload = new Dictionary<string, object?>
            {
                ["Text"] = text,
                ["VoiceId"] = voiceId,
                ["Bitrate"] = bitrate,
                ["Speed"] = speed,
                ["Pitch"] = pitch,
                ["AudioFormat"] = "mp3",
                ["OutputFormat"] = "uri",
                ["TimestampType"] = timestampType
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "speech")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, UnrealSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} speech failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var outputUri = ReadString(doc.RootElement, "OutputUri")
                            ?? ReadString(doc.RootElement, "outputUri")
                            ?? throw new InvalidOperationException($"{ProviderName} /speech returned no OutputUri: {body}");

            audioBytes = await DownloadAudioAsync(outputUri, cancellationToken);
            apiResult = JsonSerializer.Deserialize<JsonElement>(body);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(codec))
                warnings.Add(new { type = "ignored", feature = "codec", reason = "codec is supported only for /stream" });
            if (temperature is not null)
                warnings.Add(new { type = "ignored", feature = "temperature", reason = "temperature is supported only for /stream" });

            var payload = new Dictionary<string, object?>
            {
                ["Text"] = text,
                ["VoiceId"] = voiceId,
                ["Bitrate"] = bitrate,
                ["Speed"] = speed,
                ["Pitch"] = pitch,
                ["AudioFormat"] = "mp3",
                ["OutputFormat"] = "uri",
                ["TimestampType"] = timestampType
            };

            using var createReq = new HttpRequestMessage(HttpMethod.Post, "synthesisTasks")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, UnrealSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            using var createResp = await _client.SendAsync(createReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var createBody = await createResp.Content.ReadAsStringAsync(cancellationToken);
            if (!createResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} synthesisTasks create failed ({(int)createResp.StatusCode}): {createBody}");

            var (taskId, finalBody, outputUri) = await PollTaskUntilCompletedAsync(createBody, cancellationToken);
            audioBytes = await DownloadAudioAsync(outputUri, cancellationToken);

            apiResult = new
            {
                endpoint,
                taskId,
                create = JsonSerializer.Deserialize<JsonElement>(createBody),
                final = JsonSerializer.Deserialize<JsonElement>(finalBody)
            };
        }

        var resolvedFormat = ResolveFormat(request.OutputFormat);
        var mime = ResolveMimeType(resolvedFormat);

        var providerMeta = new
        {
            endpoint,
            charCount,
            voiceId,
            bitrate,
            speed,
            pitch,
            timestampType,
            codec,
            temperature,
            bytes = audioBytes.Length,
            result = apiResult
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMeta)
            }
        };
    }


    private async Task<(string TaskId, string FinalBody, string OutputUri)> PollTaskUntilCompletedAsync(
        string createBody,
        CancellationToken cancellationToken)
    {
        var taskId = TryExtractTaskId(createBody)
            ?? throw new InvalidOperationException($"{ProviderName} synthesisTasks create response missing TaskId: {createBody}");

        var timeoutAt = DateTime.UtcNow.AddMinutes(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var statusResp = await _client.GetAsync($"synthesisTasks/{Uri.EscapeDataString(taskId)}", cancellationToken);
            var statusBody = await statusResp.Content.ReadAsStringAsync(cancellationToken);

            if (!statusResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} synthesisTasks status failed ({(int)statusResp.StatusCode}): {statusBody}");

            var status = TryExtractTaskStatus(statusBody);
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var outputUri = TryExtractOutputUri(statusBody)
                    ?? throw new InvalidOperationException($"{ProviderName} synthesisTasks completed without OutputUri: {statusBody}");
                return (taskId, statusBody, outputUri);
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{ProviderName} synthesisTasks failed. TaskId={taskId}, status={status}, body={statusBody}");
            }

            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException($"{ProviderName} synthesisTasks timed out. TaskId={taskId}");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<byte[]> DownloadAudioAsync(string outputUri, CancellationToken cancellationToken)
    {
        using var audioResp = await _client.GetAsync(outputUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        return audioBytes;
    }

    private static string? TryExtractTaskId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadString(doc.RootElement, "TaskId")
               ?? (TryGetPropertyIgnoreCase(doc.RootElement, "SynthesisTask", out var task)
                   ? ReadString(task, "TaskId")
                   : null);
    }

    private static string? TryExtractTaskStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadString(doc.RootElement, "TaskStatus")
               ?? (TryGetPropertyIgnoreCase(doc.RootElement, "SynthesisTask", out var task)
                   ? ReadString(task, "TaskStatus")
                   : null);
    }

    private static string? TryExtractOutputUri(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var top = TryReadOutputUri(doc.RootElement);
        if (!string.IsNullOrWhiteSpace(top))
            return top;

        if (TryGetPropertyIgnoreCase(doc.RootElement, "SynthesisTask", out var task))
            return TryReadOutputUri(task);

        return null;
    }

    private static string? TryReadOutputUri(JsonElement node)
    {
        if (!TryGetPropertyIgnoreCase(node, "OutputUri", out var outputUri))
            return null;

        if (outputUri.ValueKind == JsonValueKind.String)
            return outputUri.GetString();

        if (outputUri.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputUri.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }

        return null;
    }

    private static string ResolveFormat(string? requestedOutputFormat)
    {
        var normalized = requestedOutputFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" or "mpeg" or null or "" => "mp3",
            "wav" or "wave" => "wav",
            "ogg" or "opus" => "ogg",
            "flac" => "flac",
            "aac" => "aac",
            _ => "mp3"
        };
    }

    private static string ResolveMimeType(string format)
        => format switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

