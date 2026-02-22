using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{
    private const string TranscriptionModelPrefix = "transcribe/source-language/";

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = NormalizeModelId(request.Model);
        var languageId = ParseLanguageIdFromModel(model);
        var (audioBytes, audioMimeFromPayload) = ParseAudioBytes(request);

        var mediaType = !string.IsNullOrWhiteSpace(request.MediaType)
            ? request.MediaType
            : audioMimeFromPayload;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(languageId.ToString(CultureInfo.InvariantCulture)), "language");

        var fileName = "audio" + ResolveAudioExtension(mediaType);
        var file = new ByteArrayContent(audioBytes);
        if (!string.IsNullOrWhiteSpace(mediaType))
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);

        form.Add(file, "media_file", fileName);

        using var createResp = await _client.PostAsync("transcribe", form, cancellationToken);
        var createJson = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription create failed ({(int)createResp.StatusCode}): {createJson}");

        var create = DeserializeOrThrow<CambaiTaskIdResponse>(createJson, "create transcription response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription create response missing task_id: {createJson}");

        var finalStatus = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetTranscriptionStatusAsync(create.TaskId!, ct),
            isTerminal: s => IsTerminalStatus(s.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(finalStatus.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription failed (task_id={create.TaskId}, status={finalStatus.Status}): {finalStatus.RawJson}");

        if (finalStatus.RunId is null || finalStatus.RunId.Value <= 0)
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription status missing run_id for successful task (task_id={create.TaskId}): {finalStatus.RawJson}");

        var (dialogues, resultJson) = await GetTranscriptionResultAsync(finalStatus.RunId.Value, cancellationToken);

        var segments = dialogues
            .Where(d => !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => new TranscriptionSegment
            {
                Text = d.Text!,
                StartSecond = d.Start,
                EndSecond = d.End
            })
            .ToList();

        var text = string.Join(' ', segments.Select(s => s.Text)).Trim();
        var duration = segments.Count > 0 ? segments.Max(s => s.EndSecond) : (float?)null;

        return new TranscriptionResponse
        {
            Text = text,
            Language = languageId.ToString(CultureInfo.InvariantCulture),
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = [],
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = new
                {
                    task_id = create.TaskId,
                    run_id = finalStatus.RunId,
                    create = createJson,
                    status = finalStatus.RawJson,
                    result = resultJson
                }
            }
        };
    }

    private async Task<CambaiTaskStatusResponse> GetTranscriptionStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"transcribe/{Uri.EscapeDataString(taskId)}", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription status failed ({(int)resp.StatusCode}): {json}");

        var status = DeserializeOrThrow<CambaiTaskStatusResponse>(json, "transcription status response");
        status.RawJson = json;
        return status;
    }

    private async Task<(IReadOnlyList<CambaiDialogueItem> Dialogues, string RawJson)> GetTranscriptionResultAsync(int runId, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"transcription-result/{runId}", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} transcription result failed ({(int)resp.StatusCode}): {json}");

        var dialogues = JsonSerializer.Deserialize<List<CambaiDialogueItem>>(json, JsonSerializerOptions.Web) ?? [];
        return (dialogues, json);
    }

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "TIMEOUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "PAYMENT_REQUIRED", StringComparison.OrdinalIgnoreCase);

    private string NormalizeModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        if (!model.Contains('/'))
            return model;

        var split = model.SplitModelId();
        return string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
            ? split.Model
            : model;
    }

    private static int ParseLanguageIdFromModel(string model)
    {
        if (!model.StartsWith(TranscriptionModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{nameof(CAMBAI)} transcription model '{model}' is not supported.");

        var languagePart = model[TranscriptionModelPrefix.Length..];
        if (!int.TryParse(languagePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var languageId) || languageId <= 0)
            throw new ArgumentException($"Invalid language id in model '{model}'.", nameof(model));

        return languageId;
    }

    private static (byte[] AudioBytes, string? MimeType) ParseAudioBytes(TranscriptionRequest request)
    {
        var audio = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        try
        {
            if (TryParseDataUrl(audio, out var mimeType, out var base64))
                return (Convert.FromBase64String(base64), mimeType);

            return (Convert.FromBase64String(audio), null);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Audio must be valid base64 or a data URL with base64 payload.", nameof(request), ex);
        }
    }

    private static bool TryParseDataUrl(string value, out string? mimeType, out string base64Payload)
    {
        mimeType = null;
        base64Payload = string.Empty;

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIndex = value.IndexOf(',');
        if (commaIndex <= 5)
            return false;

        var meta = value[5..commaIndex];
        if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            return false;

        var semicolon = meta.IndexOf(';');
        mimeType = semicolon > 0 ? meta[..semicolon] : null;
        base64Payload = value[(commaIndex + 1)..];
        return !string.IsNullOrWhiteSpace(base64Payload);
    }

    private static string ResolveAudioExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ".bin";

        try
        {
            return mediaType.GetAudioExtension();
        }
        catch (NotSupportedException)
        {
            return ".bin";
        }
    }

    private static T DeserializeOrThrow<T>(string json, string context)
        where T : class
        => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"{nameof(CAMBAI)} could not parse {context}: {json}");

    private sealed class CambaiTaskIdResponse
    {
        [JsonPropertyName("task_id")]
        public string? TaskId { get; set; }
    }

    private sealed class CambaiTaskStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("run_id")]
        public int? RunId { get; set; }

        [JsonIgnore]
        public string RawJson { get; set; } = string.Empty;
    }

    private sealed class CambaiDialogueItem
    {
        [JsonPropertyName("start")]
        public float Start { get; set; }

        [JsonPropertyName("end")]
        public float End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("speaker")]
        public string? Speaker { get; set; }
    }
}
