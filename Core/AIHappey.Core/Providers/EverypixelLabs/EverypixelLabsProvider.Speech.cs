using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model.Providers.EverypixelLabs;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        var (audio, _) = response.ToOpenAISpeechAudio();

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

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
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required and is sent as the EverypixelLabs speaker.", nameof(request));

        var model = NormalizeEverypixelModel(request.Model);
        if (!string.Equals(model, "tts_create", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EverypixelLabs speech supports only the 'tts_create' catalog model.", nameof(request));

        var speaker = request.Voice.Trim();

        var metadata = request.GetProviderMetadata<EverypixelLabsSpeechProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["speaker"] = speaker
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Style)) payload["style"] = metadata.Style.Trim();
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["language"] = request.Language.Trim();
        if (!string.IsNullOrWhiteSpace(metadata?.Prompt)) payload["prompt"] = metadata.Prompt.Trim();
        else if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["prompt"] = request.Instructions.Trim();
        if (metadata?.Seed is not null) payload["seed"] = metadata.Seed.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.CallbackUrl)) payload["callback_url"] = metadata.CallbackUrl.Trim();

        var requestBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts_create")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
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
            speaker,
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
            Request = new()
            {
                Body = requestBody
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerBody)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = audioResp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
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

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "ignored", feature = "outputFormat" });

        return warnings;
    }

    private string NormalizeEverypixelModel(string model)
    {
        var trimmed = model.Trim();
        var prefix = GetIdentifier() + "/";

        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.SplitModelId().Model;

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Model must contain an EverypixelLabs model id.", nameof(model));

        return trimmed;
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("Voice is required.", nameof(options));
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

    private static List<Uri> ExtractEverypixelResultUrls(JsonElement result, JsonElement root)
    {
        var values = new List<string>();
        CollectEverypixelUrls(result, values);
        if (values.Count == 0 && TryGetPropertyIgnoreCase(root, "result", out var nested))
            CollectEverypixelUrls(nested, values);

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                ? absolute
                : Uri.TryCreate(new Uri("https://api.everypixel.com/"), value, out var relative) ? relative : null)
            .Where(uri => uri is not null)
            .Cast<Uri>()
            .ToList();
    }

    private static void CollectEverypixelUrls(JsonElement element, List<string> values)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectEverypixelUrls(item, values);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var key in new[] { "url", "image_url", "video_url", "audio_url", "file_url", "file", "path", "urls", "images", "videos", "output" })
            if (TryGetPropertyIgnoreCase(element, key, out var value)) CollectEverypixelUrls(value, values);
    }

    private static void CopyEverypixelProviderOptions(
        Dictionary<string, JsonElement>? providerOptions,
        Dictionary<string, object?> payload,
        params string[] allowedKeys)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue(ProviderId, out var options)
            || options.ValueKind != JsonValueKind.Object) return;

        foreach (var key in allowedKeys)
        {
            if (TryGetPropertyIgnoreCase(options, key, out var value)
                && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                payload[key] = value.Clone();
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
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

