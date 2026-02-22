using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{
    private const string TtsModelPrefix = "tts/source-language/";
    private const string TranslatedTtsModelPrefix = "translated-tts/source-language/";
    private const string TextToSoundSoundModel = "text-to-sound/sound";
    private const string TextToSoundMusicModel = "text-to-sound/music";
    private const string TextToVoiceModel = "text-to-voice";

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var model = NormalizeModelId(request.Model);

        if (model.StartsWith(TtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            return await TtsRequestAsync(request, model, cancellationToken);

        if (model.StartsWith(TranslatedTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            return await TranslatedTtsRequestAsync(request, model, cancellationToken);

        if (string.Equals(model, TextToSoundSoundModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, TextToSoundMusicModel, StringComparison.OrdinalIgnoreCase))
            return await TextToSoundRequestAsync(request, model, cancellationToken);

        if (string.Equals(model, TextToVoiceModel, StringComparison.OrdinalIgnoreCase))
            return await TextToVoiceRequestAsync(request, model, cancellationToken);

        throw new NotSupportedException($"{nameof(CAMBAI)} speech model '{model}' is not supported.");
    }

    private async Task<SpeechResponse> TtsRequestAsync(SpeechRequest request, string normalizedModel, CancellationToken cancellationToken)
    {
        var languageId = ParseSourceLanguageIdFromTtsModel(normalizedModel);
        var warnings = BuildCommonSpeechWarnings(request);

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from model id" });

        var voiceId = ResolveVoiceId(request.Voice, fallback: 147320);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_id"] = voiceId,
            ["language"] = languageId
        };

        var createJson = await PostJsonAndReadAsync("tts", payload, cancellationToken);
        var create = DeserializeOrThrow<CambaiSpeechTaskIdResponse>(createJson, "create TTS response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} TTS create response missing task_id: {createJson}");

        var finalStatus = await PollTaskStatusAsync(
            taskId: create.TaskId!,
            statusPathTemplate: "tts/{0}",
            operationName: "TTS",
            cancellationToken: cancellationToken);

        var (audioBytes, resultBody) = await GetBinaryResultAsync(
            runId: finalStatus.RunId!.Value,
            resultPathTemplate: "tts-result/{0}",
            operationName: "TTS result",
            cancellationToken: cancellationToken);

        return BuildSpeechResponse(
            requestModel: request.Model,
            normalizedModel: normalizedModel,
            audioBytes: audioBytes,
            fallbackFormat: ResolveFormatFromOutput(request.OutputFormat, "flac"),
            warnings: warnings,
            body: new
            {
                task_id = create.TaskId,
                run_id = finalStatus.RunId,
                create = createJson,
                status = finalStatus.RawJson,
                result = resultBody
            });
    }

    private async Task<SpeechResponse> TranslatedTtsRequestAsync(SpeechRequest request, string normalizedModel, CancellationToken cancellationToken)
    {
        var (sourceLanguageId, targetLanguageId) = ParseSourceAndTargetLanguageIdsFromTranslatedTtsModel(normalizedModel);
        var warnings = BuildCommonSpeechWarnings(request);

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "source/target languages are derived from model id" });

        var voiceId = ResolveVoiceId(request.Voice, fallback: 147320);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_id"] = voiceId,
            ["source_language"] = sourceLanguageId,
            ["target_language"] = targetLanguageId
        };

        var createJson = await PostJsonAndReadAsync("translated-tts", payload, cancellationToken);
        var create = DeserializeOrThrow<CambaiSpeechTaskIdResponse>(createJson, "create translated TTS response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} translated TTS create response missing task_id: {createJson}");

        var finalStatus = await PollTaskStatusAsync(
            taskId: create.TaskId!,
            statusPathTemplate: "translated-tts/{0}",
            operationName: "translated TTS",
            cancellationToken: cancellationToken);

        var (audioBytes, resultBody) = await GetBinaryResultAsync(
            runId: finalStatus.RunId!.Value,
            resultPathTemplate: "tts-result/{0}",
            operationName: "translated TTS result",
            cancellationToken: cancellationToken);

        return BuildSpeechResponse(
            requestModel: request.Model,
            normalizedModel: normalizedModel,
            audioBytes: audioBytes,
            fallbackFormat: ResolveFormatFromOutput(request.OutputFormat, "flac"),
            warnings: warnings,
            body: new
            {
                task_id = create.TaskId,
                run_id = finalStatus.RunId,
                create = createJson,
                status = finalStatus.RawJson,
                result = resultBody
            });
    }

    private async Task<SpeechResponse> TextToSoundRequestAsync(SpeechRequest request, string normalizedModel, CancellationToken cancellationToken)
    {
        var warnings = BuildCommonSpeechWarnings(request);

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var audioType = string.Equals(normalizedModel, TextToSoundMusicModel, StringComparison.OrdinalIgnoreCase)
            ? "music"
            : "sound";

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Text,
            ["audio_type"] = audioType
        };

        var createJson = await PostJsonAndReadAsync("text-to-sound", payload, cancellationToken);
        var create = DeserializeOrThrow<CambaiSpeechTaskIdResponse>(createJson, "create text-to-sound response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} text-to-sound create response missing task_id: {createJson}");

        var finalStatus = await PollTaskStatusAsync(
            taskId: create.TaskId!,
            statusPathTemplate: "text-to-sound/{0}",
            operationName: "text-to-sound",
            cancellationToken: cancellationToken);

        var (audioBytes, resultBody) = await GetBinaryResultAsync(
            runId: finalStatus.RunId!.Value,
            resultPathTemplate: "text-to-sound-result/{0}",
            operationName: "text-to-sound result",
            cancellationToken: cancellationToken);

        return BuildSpeechResponse(
            requestModel: request.Model,
            normalizedModel: normalizedModel,
            audioBytes: audioBytes,
            fallbackFormat: "wav",
            warnings: warnings,
            body: new
            {
                task_id = create.TaskId,
                run_id = finalStatus.RunId,
                create = createJson,
                status = finalStatus.RawJson,
                result = resultBody,
                audio_type = audioType
            });
    }

    private async Task<SpeechResponse> TextToVoiceRequestAsync(SpeechRequest request, string normalizedModel, CancellationToken cancellationToken)
    {
        var warnings = BuildCommonSpeechWarnings(request);

        if (string.IsNullOrWhiteSpace(request.Instructions))
            throw new ArgumentException("Instructions are required for CAMBAI text-to-voice and are mapped to voice_description.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_description"] = request.Instructions
        };

        var createJson = await PostJsonAndReadAsync("text-to-voice", payload, cancellationToken);
        var create = DeserializeOrThrow<CambaiSpeechTaskIdResponse>(createJson, "create text-to-voice response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} text-to-voice create response missing task_id: {createJson}");

        var finalStatus = await PollTaskStatusAsync(
            taskId: create.TaskId!,
            statusPathTemplate: "text-to-voice/{0}",
            operationName: "text-to-voice",
            cancellationToken: cancellationToken);

        var runInfoJson = await GetJsonResultAsync(
            runId: finalStatus.RunId!.Value,
            resultPathTemplate: "text-to-voice-result/{0}",
            operationName: "text-to-voice result",
            cancellationToken: cancellationToken);

        var runInfo = DeserializeOrThrow<CambaiTextToVoiceResultResponse>(runInfoJson, "text-to-voice run result");
        var previewUrl = runInfo.Previews?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(previewUrl))
            throw new InvalidOperationException($"{nameof(CAMBAI)} text-to-voice result returned no preview URLs: {runInfoJson}");

        using var previewResp = await _client.GetAsync(previewUrl, cancellationToken);
        var previewBytes = await previewResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!previewResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(previewBytes);
            throw new InvalidOperationException($"{nameof(CAMBAI)} text-to-voice preview download failed ({(int)previewResp.StatusCode}): {err}");
        }

        return BuildSpeechResponse(
            requestModel: request.Model,
            normalizedModel: normalizedModel,
            audioBytes: previewBytes,
            fallbackFormat: "mp3",
            warnings: warnings,
            body: new
            {
                task_id = create.TaskId,
                run_id = finalStatus.RunId,
                create = createJson,
                status = finalStatus.RawJson,
                result = runInfoJson,
                selected_preview = previewUrl
            });
    }

    private async Task<CambaiSpeechTaskStatusResponse> PollTaskStatusAsync(
        string taskId,
        string statusPathTemplate,
        string operationName,
        CancellationToken cancellationToken)
    {
        var finalStatus = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: async ct =>
            {
                using var resp = await _client.GetAsync(string.Format(CultureInfo.InvariantCulture, statusPathTemplate, Uri.EscapeDataString(taskId)), ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{nameof(CAMBAI)} {operationName} status failed ({(int)resp.StatusCode}): {json}");

                var status = DeserializeOrThrow<CambaiSpeechTaskStatusResponse>(json, $"{operationName} status response");
                status.RawJson = json;
                return status;
            },
            isTerminal: s => IsTerminalStatus(s.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(finalStatus.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{nameof(CAMBAI)} {operationName} failed (task_id={taskId}, status={finalStatus.Status}): {finalStatus.RawJson}");

        if (finalStatus.RunId is null || finalStatus.RunId.Value <= 0)
            throw new InvalidOperationException($"{nameof(CAMBAI)} {operationName} status missing run_id for successful task (task_id={taskId}): {finalStatus.RawJson}");

        return finalStatus;
    }

    private async Task<(byte[] Bytes, string RawBody)> GetBinaryResultAsync(
        int runId,
        string resultPathTemplate,
        string operationName,
        CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, resultPathTemplate, runId);
        using var resp = await _client.GetAsync(path, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} {operationName} failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, Encoding.UTF8.GetString(bytes));
    }

    private async Task<string> GetJsonResultAsync(
        int runId,
        string resultPathTemplate,
        string operationName,
        CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, resultPathTemplate, runId);
        using var resp = await _client.GetAsync(path, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} {operationName} failed ({(int)resp.StatusCode}): {json}");

        return json;
    }

    private async Task<string> PostJsonAndReadAsync(string relativePath, object payload, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} {relativePath} failed ({(int)resp.StatusCode}): {json}");

        return json;
    }

    private static List<object> BuildCommonSpeechWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "ignored", feature = "outputFormat" });

        return warnings;
    }

    private static int ParseSourceLanguageIdFromTtsModel(string model)
    {
        if (!model.StartsWith(TtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{nameof(CAMBAI)} speech model '{model}' is not a TTS model.");

        var languagePart = model[TtsModelPrefix.Length..];
        if (!int.TryParse(languagePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var languageId) || languageId <= 0)
            throw new ArgumentException($"Invalid source language id in model '{model}'.", nameof(model));

        return languageId;
    }

    private static (int SourceLanguageId, int TargetLanguageId) ParseSourceAndTargetLanguageIdsFromTranslatedTtsModel(string model)
    {
        if (!model.StartsWith(TranslatedTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{nameof(CAMBAI)} speech model '{model}' is not a translated TTS model.");

        var remainder = model[TranslatedTtsModelPrefix.Length..];
        var splitMarker = "/target-language/";
        var markerIndex = remainder.IndexOf(splitMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            throw new ArgumentException($"Invalid translated TTS model '{model}'.", nameof(model));

        var sourcePart = remainder[..markerIndex];
        var targetPart = remainder[(markerIndex + splitMarker.Length)..];

        if (!int.TryParse(sourcePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceLanguageId) || sourceLanguageId <= 0)
            throw new ArgumentException($"Invalid source language id in model '{model}'.", nameof(model));

        if (!int.TryParse(targetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetLanguageId) || targetLanguageId <= 0)
            throw new ArgumentException($"Invalid target language id in model '{model}'.", nameof(model));

        return (sourceLanguageId, targetLanguageId);
    }

    private static int ResolveVoiceId(string? voice, int fallback)
    {
        if (string.IsNullOrWhiteSpace(voice))
            return fallback;

        if (!int.TryParse(voice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var voiceId) || voiceId <= 0)
            throw new ArgumentException($"Voice '{voice}' must be a positive integer CAMB.AI voice_id.", nameof(voice));

        return voiceId;
    }

    private static SpeechResponse BuildSpeechResponse(
        string requestModel,
        string normalizedModel,
        byte[] audioBytes,
        string fallbackFormat,
        IReadOnlyList<object> warnings,
        object body)
    {
        var format = ResolveFormatFromOutput(null, fallbackFormat);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = GuessMimeType(format),
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = requestModel,
                Body = body
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["cambai"] = JsonSerializer.SerializeToElement(new
                {
                    model = normalizedModel
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private static string ResolveFormatFromOutput(string? requestedFormat, string fallback)
    {
        if (string.IsNullOrWhiteSpace(requestedFormat))
            return fallback;

        var fmt = requestedFormat.Trim().ToLowerInvariant();
        return fmt switch
        {
            "wav" or "wave" => "wav",
            "mp3" => "mp3",
            "flac" => "flac",
            "aac" => "aac",
            "opus" => "opus",
            _ => fallback
        };
    }

    private static string GuessMimeType(string format)
        => format switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "opus" => "audio/opus",
            _ => "application/octet-stream"
        };

    private sealed class CambaiSpeechTaskIdResponse
    {
        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }
    }

    private sealed class CambaiSpeechTaskStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("run_id")]
        public int? RunId { get; set; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }

    private sealed class CambaiTextToVoiceResultResponse
    {
        [JsonPropertyName("previews")]
        public List<string>? Previews { get; set; }
    }
}
