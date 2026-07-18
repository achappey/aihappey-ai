using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Crazyrouter;

public partial class CrazyrouterProvider
{
    private const string CrazyrouterSunoModel = "crazyrouter/suno";
    private const int DefaultCrazyrouterSunoPollIntervalSeconds = 10;
    private const int DefaultCrazyrouterSunoPollTimeoutMinutes = 5;

    private static readonly JsonSerializerOptions CrazyrouterSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record CrazyrouterSunoTaskResult(
        string? TaskId,
        string Status,
        JsonElement Root,
        Dictionary<string, string>? Headers);

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
            AudioSpeechRequest options,
            CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent>
        OpenAISpeechStreamingAsync(
            AudioSpeechRequest options,
            CancellationToken cancellationToken = default)
        => this.SpeechStreamingAsync(options, cancellationToken);

    private async Task<SpeechResponse> CrazyrouterSpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsCrazyrouterSunoModel(request.Model))
            return await CrazyrouterSunoSpeechRequest(request, cancellationToken);

        return await CrazyrouterOpenAISpeechRequest(request, cancellationToken);
    }

    private async Task<SpeechResponse> CrazyrouterOpenAISpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<CrazyrouterSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var voice = !string.IsNullOrWhiteSpace(request.Voice)
            ? request.Voice!.Trim()
            : metadata?.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for Crazyrouter speech endpoint.", nameof(request));

        var outputFormat = string.IsNullOrWhiteSpace(request.OutputFormat)
            ? (string.IsNullOrWhiteSpace(metadata?.ResponseFormat) ? "mp3" : metadata!.ResponseFormat!.Trim().ToLowerInvariant())
            : request.OutputFormat.Trim().ToLowerInvariant();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = outputFormat
        };

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;
        else if (metadata?.Speed is not null)
            payload["speed"] = metadata.Speed.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CrazyrouterSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var contentBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = resp.Content.Headers.ContentType?.MediaType;

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(contentBytes);
            throw new InvalidOperationException($"Crazyrouter speech request failed ({(int)resp.StatusCode}): {err}");
        }

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(contentBytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new
                {
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    contentLength = contentBytes.LongLength
                }
            }
        };
    }

    private async Task<SpeechResponse> CrazyrouterSunoSpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (string.IsNullOrWhiteSpace(request.Text)
            && string.IsNullOrWhiteSpace(TryGetCrazyrouterSunoString(request, "prompt"))
            && string.IsNullOrWhiteSpace(TryGetCrazyrouterSunoString(request, "gpt_description_prompt")))
            throw new ArgumentException("Text is required for Crazyrouter Suno inspiration mode unless providerOptions.crazyrouter.prompt or gpt_description_prompt is provided.", nameof(request));

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = BuildCrazyrouterSunoWarnings(request);
        var payload = BuildCrazyrouterSunoPayload(request, providerOptions);

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "suno/submit/music")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CrazyrouterSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var startResponse = await _client.SendAsync(startRequest, cancellationToken);
        var startRaw = await startResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Crazyrouter Suno submit request failed ({(int)startResponse.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var startRoot = startDoc.RootElement.Clone();

        var taskId = TryGetString(startRoot, "result");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Crazyrouter Suno submit response missing result task id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollCrazyrouterSunoTaskAsync(taskId, ct),
            isTerminal: r => IsCrazyrouterSunoTerminalStatus(r.Status),
            interval: TimeSpan.FromSeconds(Math.Max(1, ResolveCrazyrouterSunoPollIntervalSeconds(providerOptions))),
            timeout: TimeSpan.FromMinutes(Math.Max(1, ResolveCrazyrouterSunoPollTimeoutMinutes(providerOptions))),
            maxAttempts: ResolveCrazyrouterSunoPollMaxAttempts(providerOptions),
            cancellationToken: cancellationToken);

        if (!string.Equals(completed.Status, "complete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Crazyrouter Suno task failed (taskId={taskId}, status={completed.Status}).");

        var audioUrl = TryGetFirstCrazyrouterSunoAudioUrl(completed.Root, request.OutputFormat);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"Crazyrouter Suno task completed but no audio_url was returned (taskId={taskId}).");

        using var fileResponse = await _client.GetAsync(audioUrl, cancellationToken);
        var fileBytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResponse.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Crazyrouter Suno audio download failed ({(int)fileResponse.StatusCode}): {err}");
        }

        var mimeType = fileResponse.Content.Headers.ContentType?.MediaType
            ?? GuessCrazyrouterAudioMimeType(audioUrl)
            ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(fileBytes),
                MimeType = mimeType,
                Format = MapCrazyrouterAudioFormat(mimeType)
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                taskId = completed.TaskId ?? taskId,
                status = completed.Status,
                submit = startRoot,
                audioUrl
            }),
            Request = new()
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = completed.Headers ?? fileResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = completed.Root.Clone()
            }
        };
    }

    private async Task<CrazyrouterSunoTaskResult> PollCrazyrouterSunoTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"suno/fetch/{Uri.EscapeDataString(taskId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Crazyrouter Suno poll request failed ({(int)pollResponse.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();

        var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
            ? dataEl
            : default;

        var status = data.ValueKind == JsonValueKind.Object
            ? TryGetString(data, "status") ?? "unknown"
            : "unknown";

        var returnedTaskId = data.ValueKind == JsonValueKind.Object
            ? TryGetString(data, "task_id") ?? taskId
            : taskId;

        return new CrazyrouterSunoTaskResult(returnedTaskId, status, root, pollResponse.GetHeaders());
    }

    private static Dictionary<string, object?> BuildCrazyrouterSunoPayload(
        SpeechRequest request,
        JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (IsCrazyrouterSunoPollControlOption(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        if (!payload.ContainsKey("gpt_description_prompt")
            && !payload.ContainsKey("prompt")
            && !string.IsNullOrWhiteSpace(request.Text))
        {
            payload["gpt_description_prompt"] = request.Text;
        }

        if (!payload.ContainsKey("mv"))
            payload["mv"] = "chirp-v4";

        return payload;
    }

    private static List<object> BuildCrazyrouterSunoWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice", details = "Crazyrouter Suno uses singer_style via providerOptions.crazyrouter.singer_style instead of the standard speech voice parameter." });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions", details = "Use providerOptions.crazyrouter.tags, singer_style, or custom Suno parameters instead." });

        return warnings;
    }

    private static string? TryGetFirstCrazyrouterSunoAudioUrl(JsonElement root, string? requestedFormat)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("data", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var clipStatus = TryGetString(item, "status");
            if (!string.IsNullOrWhiteSpace(clipStatus)
                && !string.Equals(clipStatus, "complete", StringComparison.OrdinalIgnoreCase))
                continue;

            var audioUrl = TryGetString(item, "audio_url");
            if (string.IsNullOrWhiteSpace(audioUrl))
                continue;

            if (string.Equals(requestedFormat, "wav", StringComparison.OrdinalIgnoreCase)
                && audioUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return audioUrl[..^4] + ".wav";
            }

            return audioUrl;
        }

        return null;
    }

    private static bool IsCrazyrouterSunoModel(string? model)
        => string.Equals(model, CrazyrouterSunoModel, StringComparison.OrdinalIgnoreCase)
           || string.Equals(model, "suno", StringComparison.OrdinalIgnoreCase);

    private static bool IsCrazyrouterSunoTerminalStatus(string? status)
        => string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static int ResolveCrazyrouterSunoPollIntervalSeconds(JsonElement metadata)
        => TryReadCrazyrouterInt(metadata, "poll_interval_seconds", "pollIntervalSeconds")
           ?? DefaultCrazyrouterSunoPollIntervalSeconds;

    private static int ResolveCrazyrouterSunoPollTimeoutMinutes(JsonElement metadata)
        => TryReadCrazyrouterInt(metadata, "poll_timeout_minutes", "pollTimeoutMinutes")
           ?? DefaultCrazyrouterSunoPollTimeoutMinutes;

    private static int? ResolveCrazyrouterSunoPollMaxAttempts(JsonElement metadata)
        => TryReadCrazyrouterInt(metadata, "poll_max_attempts", "pollMaxAttempts");

    private static int? TryReadCrazyrouterInt(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!metadata.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                return parsed;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed))
                return parsed;
        }

        return null;
    }

    private static bool IsCrazyrouterSunoPollControlOption(string name)
        => string.Equals(name, "poll_interval_seconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollIntervalSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_timeout_minutes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollTimeoutMinutes", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "poll_max_attempts", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "pollMaxAttempts", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetCrazyrouterSunoString(SpeechRequest request, string name)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(nameof(Crazyrouter).ToLowerInvariant());
        return TryGetString(metadata, name);
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string MapCrazyrouterAudioFormat(string mimeType)
    {
        var mt = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return mt switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" => "m4a",
            "audio/webm" => "webm",
            _ => "mp3"
        };
    }

    private static string? GuessCrazyrouterAudioMimeType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp3")) return "audio/mpeg";
        if (u.Contains(".wav")) return "audio/wav";
        if (u.Contains(".ogg")) return "audio/ogg";
        if (u.Contains(".opus")) return "audio/opus";
        if (u.Contains(".flac")) return "audio/flac";
        if (u.Contains(".aac")) return "audio/aac";
        if (u.Contains(".m4a") || u.Contains(".mp4")) return "audio/mp4";
        if (u.Contains(".webm")) return "audio/webm";
        return null;
    }

    private sealed class CrazyrouterSpeechProviderMetadata
    {
        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }

        [JsonPropertyName("speed")]
        public float? Speed { get; set; }
    }
}
