using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.EverypixelLabs;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
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
        var warnings = BuildWarnings(request);
        var voiceId = ParseVoiceIdFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var metadata = request.GetProviderMetadata<EverypixelLabsSpeechProviderMetadata>(GetIdentifier());

        var form = new List<KeyValuePair<string, string>>
        {
            new("text", request.Text),
            new("voice", voiceId)
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Title))
            form.Add(new KeyValuePair<string, string>("title", metadata.Title.Trim()));

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts/create")
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var createResp = await _client.SendAsync(createRequest, cancellationToken);
        var createJson = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} tts create failed ({(int)createResp.StatusCode}): {createJson}");

        var create = DeserializeOrThrow<EverypixelTaskStatusResponse>(createJson, "tts create response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{ProviderName} tts create response missing task_id: {createJson}");

        var finalStatus = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetTaskStatusAsync(create.TaskId!, ct),
            isTerminal: s => IsTerminalStatus(s.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(finalStatus.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} TTS failed (task_id={create.TaskId}, status={finalStatus.Status}): {finalStatus.RawJson}");

        var resultUri = ExtractAudioUri(finalStatus.Result, finalStatus.RawRoot);
        if (resultUri is null)
            throw new InvalidOperationException($"{ProviderName} status result has no audio URL: {finalStatus.RawJson}");

        using var audioResp = await _client.GetAsync(resultUri, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var mimeType = ResolveMimeType(audioResp.Content.Headers.ContentType?.MediaType, resultUri);
        var format = ResolveFormat(mimeType, resultUri);

        var providerBody = new
        {
            voice = voiceId,
            task_id = create.TaskId,
            create_status = create.Status,
            queue = create.Queue,
            create = createJson,
            status = finalStatus.RawJson,
            audio_url = resultUri.ToString()
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerBody)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerBody)
            }
        };
    }

    private async Task<EverypixelTaskStatusResponse> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        var encodedTaskId = Uri.EscapeDataString(taskId);
        using var resp = await _client.GetAsync($"v1/status?task_id={encodedTaskId}", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} status failed ({(int)resp.StatusCode}): {json}");

        var status = DeserializeOrThrow<EverypixelTaskStatusResponse>(json, "status response");
        status.RawJson = json;

        using var doc = JsonDocument.Parse(json);
        status.RawRoot = doc.RootElement.Clone();

        return status;
    }

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "FAILURE", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "REVOKED", StringComparison.OrdinalIgnoreCase);

    private static List<object> BuildWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "ignored", feature = "outputFormat" });

        return warnings;
    }

    private string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();
        var prefix = GetIdentifier() + "/";

        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.SplitModelId().Model;

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Model must contain an EverypixelLabs voice id.", nameof(model));

        return trimmed;
    }

    private static Uri? ExtractAudioUri(JsonElement result, JsonElement root)
    {
        if (result.ValueKind == JsonValueKind.String)
        {
            var resultString = result.GetString();

            if (Uri.TryCreate(resultString, UriKind.Absolute, out var absoluteFromString))
                return absoluteFromString;

            if (!string.IsNullOrWhiteSpace(resultString)
                && Uri.TryCreate(new Uri("https://api.everypixel.com/"), resultString, out var relativeFromString))
            {
                return relativeFromString;
            }
        }

        if (result.ValueKind == JsonValueKind.Object && TryReadObjectUrl(result, out var objectUrl))
        {
            if (Uri.TryCreate(objectUrl, UriKind.Absolute, out var absoluteObjectUrl))
                return absoluteObjectUrl;

            if (Uri.TryCreate(new Uri("https://api.everypixel.com/"), objectUrl, out var relativeObjectUrl))
                return relativeObjectUrl;
        }

        if (TryReadResultString(root, out var nestedResult)
            && Uri.TryCreate(nestedResult, UriKind.Absolute, out var nestedAbsolute))
            return nestedAbsolute;

        if (TryReadResultString(root, out nestedResult)
            && Uri.TryCreate(new Uri("https://api.everypixel.com/"), nestedResult, out var nestedRelative))
            return nestedRelative;

        return null;
    }

    private static bool TryReadResultString(JsonElement root, out string value)
    {
        if (TryGetPropertyIgnoreCase(root, "result", out var resultEl))
        {
            if (resultEl.ValueKind == JsonValueKind.String)
            {
                var s = resultEl.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    value = s;
                    return true;
                }
            }

            if (resultEl.ValueKind == JsonValueKind.Object)
            {
                if (TryReadObjectUrl(resultEl, out value))
                    return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadObjectUrl(JsonElement obj, out string value)
    {
        foreach (var key in new[] { "url", "audio_url", "audioUrl", "file", "file_url", "path" })
        {
            if (TryGetPropertyIgnoreCase(obj, key, out var el)
                && el.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(el.GetString()))
            {
                value = el.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string ResolveMimeType(string? contentType, Uri audioUri)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var ext = Path.GetExtension(audioUri.AbsolutePath).Trim('.').ToLowerInvariant();
        return ext switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveFormat(string mimeType, Uri audioUri)
    {
        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("flac")) return "flac";
        if (mt.Contains("aac")) return "aac";
        if (mt.Contains("pcm")) return "pcm";

        var ext = Path.GetExtension(audioUri.AbsolutePath).Trim('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(ext)
            ? "mp3"
            : ext switch
            {
                "mpeg" => "mp3",
                "wave" => "wav",
                _ => ext
            };
    }

    private static T DeserializeOrThrow<T>(string json, string context)
        => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"{ProviderName} could not parse {context}: {json}");

    private sealed class EverypixelTaskStatusResponse
    {
        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("queue")]
        public int? Queue { get; set; }

        [JsonPropertyName("result")]
        public JsonElement Result { get; set; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;

        [JsonIgnore]
        public JsonElement RawRoot { get; set; }
    }
}

