using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.LOVO;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LOVO;

public partial class LOVOProvider
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

        var normalizedModel = request.Model;
        var speakerId = ParseSpeakerIdFromModel(normalizedModel);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected speaker model" });

        if (!string.IsNullOrWhiteSpace(request.Voice) && !string.Equals(request.Voice.Trim(), speakerId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var metadata = request.GetProviderMetadata<LOVOSpeechProviderMetadata>(GetIdentifier());
        var speakerStyle = metadata?.SpeakerStyle?.Trim();

        if (request.Speed is { } speed && (speed < 0.05f || speed > 3f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "LOVO speed must be between 0.05 and 3.");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["speaker"] = speakerId,
            ["speed"] = request.Speed ?? 1f,
            ["speakerStyle"] = string.IsNullOrWhiteSpace(speakerStyle) ? null : speakerStyle
        };

        var syncJson = await PostJsonAndReadAsync("api/v1/tts/sync", payload, cancellationToken);
        using var syncDoc = JsonDocument.Parse(syncJson);

        var audioUrl = TryFindAudioUrl(syncDoc.RootElement);
        string? jobId = null;
        string? finalJson = null;

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            jobId = TryFindJobId(syncDoc.RootElement);
            if (string.IsNullOrWhiteSpace(jobId))
                throw new InvalidOperationException($"{ProviderName} sync TTS returned neither audio URL nor job id: {syncJson}");

            finalJson = await PollTtsJobForResultJsonAsync(jobId!, cancellationToken);
            using var finalDoc = JsonDocument.Parse(finalJson);
            audioUrl = TryFindAudioUrl(finalDoc.RootElement);

            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new InvalidOperationException($"{ProviderName} async TTS job completed without audio URL. JobId={jobId}. Body={finalJson}");
        }

        using var audioResp = await _client.GetAsync(audioUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var mime = ResolveMimeType(audioResp.Content.Headers.ContentType?.MediaType, audioUrl, request.OutputFormat);
        var format = ResolveFormat(mime, audioUrl, request.OutputFormat);

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
                    speakerId,
                    speakerStyle,
                    sync = JsonSerializer.Deserialize<JsonElement>(syncJson),
                    asyncResult = finalJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(finalJson),
                    audioUrl
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(new
                {
                    sync = JsonSerializer.Deserialize<JsonElement>(syncJson),
                    asyncResult = finalJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(finalJson),
                    audioUrl
                })
            }
        };
    }

    private static string ParseSpeakerIdFromModel(string model)
    {
        if (!model.StartsWith(LovoTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{LovoTtsModelPrefix}[speakerId]'.");

        var speakerId = model[LovoTtsModelPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(speakerId))
            throw new ArgumentException("Model must contain a speaker id after 'tts/'.", nameof(model));

        return speakerId;
    }

    private async Task<string> PollTtsJobForResultJsonAsync(string jobId, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(90);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync($"api/v1/tts/{Uri.EscapeDataString(jobId)}", cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} async job retrieval failed ({(int)resp.StatusCode}) for job {jobId}: {body}");

            using var doc = JsonDocument.Parse(body);

            if (!string.IsNullOrWhiteSpace(TryFindAudioUrl(doc.RootElement)))
                return body;

            var status = TryFindStatus(doc.RootElement);
            if (IsFailedStatus(status))
                throw new InvalidOperationException($"{ProviderName} async TTS job failed. JobId={jobId}, status={status}, body={body}");

            if (DateTime.UtcNow - started >= timeout)
                throw new TimeoutException($"{ProviderName} async TTS job timed out after {timeout.TotalSeconds:0} seconds. JobId={jobId}.");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
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

    private static string? TryFindAudioUrl(JsonElement root)
    {
        foreach (var candidate in EnumerateUrls(root))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateUrls(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = node.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    yield return s;
                yield break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var value in EnumerateUrls(item))
                        yield return value;
                }

                yield break;
            }
            case JsonValueKind.Object:
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("audio", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("result", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("output", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Contains("data", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var value in EnumerateUrls(prop.Value))
                            yield return value;
                    }
                }

                yield break;
            }
            default:
                yield break;
        }
    }

    private static string? TryFindJobId(JsonElement root)
        => ReadString(root, "id")
           ?? ReadString(root, "jobId")
           ?? ReadString(root, "job_id")
           ?? (TryGetPropertyIgnoreCase(root, "data", out var data) ? ReadString(data, "id") ?? ReadString(data, "jobId") ?? ReadString(data, "job_id") : null);

    private static string? TryFindStatus(JsonElement root)
        => ReadString(root, "status")
           ?? ReadString(root, "state")
           ?? (TryGetPropertyIgnoreCase(root, "data", out var data) ? ReadString(data, "status") ?? ReadString(data, "state") : null);

    private static bool IsFailedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Trim().ToLowerInvariant() switch
        {
            "failed" or "error" or "cancelled" or "canceled" => true,
            _ => false
        };
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

