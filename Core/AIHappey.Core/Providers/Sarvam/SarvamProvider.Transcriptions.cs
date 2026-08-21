using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Sarvam;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    private const string SpeechToTextJobPath = "speech-to-text/job/v1";
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(250);

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<SarvamTranscriptionProviderMetadata>(GetIdentifier());
        var bytes = DecodeAudio(request.Audio);
        var fileName = GetAudioFileName(request.MediaType, metadata?.InputAudioCodec);
        var model = NormalizeModelId(metadata?.Model ?? request.Model);

        var jobParameters = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "saaras:v4" : model,
            ["language_code"] = metadata?.LanguageCode ?? "unknown",
            ["mode"] = metadata?.Mode ?? "transcribe",
            ["with_timestamps"] = metadata?.WithTimestamps ?? true,
            ["with_diarization"] = metadata?.WithDiarization ?? false
        };
        if (metadata?.NumberOfSpeakers is not null)
            jobParameters["num_speakers"] = metadata.NumberOfSpeakers;
        if (!string.IsNullOrWhiteSpace(metadata?.InputAudioCodec))
            jobParameters["input_audio_codec"] = metadata.InputAudioCodec;

        using var initiated = await SendJobJsonAsync(HttpMethod.Post, SpeechToTextJobPath,
            new Dictionary<string, object?> { ["job_parameters"] = jobParameters }, cancellationToken);
        var jobId = GetRequiredString(initiated.RootElement, "job_id", "Sarvam did not return a job id.");

        using var uploadLinks = await SendJobJsonAsync(HttpMethod.Post, $"{SpeechToTextJobPath}/upload-files",
            new Dictionary<string, object?> { ["job_id"] = jobId, ["files"] = new[] { fileName } }, cancellationToken);
        var uploadUrl = GetFileUrl(uploadLinks.RootElement, "upload_urls", fileName);
        await UploadAudioAsync(uploadUrl, bytes, request.MediaType, cancellationToken);

        using (await SendJobJsonAsync(HttpMethod.Post, $"{SpeechToTextJobPath}/{Uri.EscapeDataString(jobId)}/start", null, cancellationToken))
        {
        }

        using var status = await WaitForJobAsync(jobId, cancellationToken);
        var outputFiles = GetOutputFiles(status.RootElement);
        if (outputFiles.Count == 0)
            throw new InvalidOperationException($"Sarvam batch STT completed without output files. Job: {jobId}");

        using var downloadLinks = await SendJobJsonAsync(HttpMethod.Post, $"{SpeechToTextJobPath}/download-files",
            new Dictionary<string, object?> { ["job_id"] = jobId, ["files"] = outputFiles }, cancellationToken);

        var results = new List<string>();
        foreach (var outputFile in outputFiles)
        {
            var downloadUrl = GetFileUrl(downloadLinks.RootElement, "download_urls", outputFile);
            using var outputResponse = await _storageClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var output = await outputResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!outputResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Sarvam STT result download failed ({(int)outputResponse.StatusCode}): {output}");
            results.Add(output);
        }

        var combinedJson = CombineTranscriptionResults(results);
        return ConvertSarvamTranscriptionResponse(
            combinedJson,
            request.Model.ToModelId(GetIdentifier()),
            GetIdentifier(),
            now,
            null);
    }

    private static byte[] DecodeAudio(object? audio)
    {
        var value = audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));
        if (MediaContentHelpers.TryParseDataUrl(value, out _, out var base64))
            value = base64;
        return Convert.FromBase64String(value);
    }

    private static string GetAudioFileName(string? mediaType, string? codec)
    {
        if (!string.IsNullOrWhiteSpace(codec))
            return "audio." + codec.Replace("pcm_", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            try { return "audio" + mediaType.GetAudioExtension(); }
            catch (NotSupportedException) { }
        }
        return "audio.wav";
    }

    private async Task<JsonDocument> SendJobJsonAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?>? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam batch STT failed ({(int)response.StatusCode}): {body}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task UploadAudioAsync(Uri uploadUrl, byte[] bytes, string? mediaType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        request.Content = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(mediaType))
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        if (uploadUrl.Host.Contains("blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        using var response = await _storageClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam STT upload failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private async Task<JsonDocument> WaitForJobAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await SendJobJsonAsync(HttpMethod.Get,
                $"{SpeechToTextJobPath}/{Uri.EscapeDataString(jobId)}/status", null, cancellationToken);
            var state = GetRequiredString(status.RootElement, "job_state", "Sarvam status omitted job_state.");
            if (state.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                || state.Equals("PartiallyCompleted", StringComparison.OrdinalIgnoreCase))
                return status;
            if (state.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = status.RootElement.TryGetProperty("error_message", out var errorElement) ? errorElement.GetString() : null;
                status.Dispose();
                throw new InvalidOperationException($"Sarvam batch STT job {jobId} failed: {error}");
            }
            status.Dispose();
            await Task.Delay(JobPollInterval, cancellationToken);
        }
    }

    private static string GetRequiredString(JsonElement root, string name, string error)
        => root.TryGetProperty(name, out var element) && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new InvalidOperationException(error);

    private static Uri GetFileUrl(JsonElement root, string collectionName, string fileName)
    {
        if (!root.TryGetProperty(collectionName, out var collection) || collection.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Sarvam response omitted {collectionName}.");
        JsonElement entry;
        if (!collection.TryGetProperty(fileName, out entry))
            entry = collection.EnumerateObject().Select(property => property.Value).FirstOrDefault();
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("file_url", out var urlElement)
            || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
            throw new InvalidOperationException($"Sarvam returned no URL for {fileName}.");
        return url;
    }

    private static List<string> GetOutputFiles(JsonElement root)
    {
        var files = new List<string>();
        if (!root.TryGetProperty("job_details", out var details) || details.ValueKind != JsonValueKind.Array)
            return files;
        foreach (var detail in details.EnumerateArray())
        {
            if (!detail.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array) continue;
            foreach (var output in outputs.EnumerateArray())
                if (output.TryGetProperty("file_name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                    files.Add(name.GetString()!);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string CombineTranscriptionResults(IReadOnlyList<string> results)
    {
        if (results.Count == 1) return results[0];
        var transcripts = new List<string>();
        string? language = null;
        foreach (var result in results)
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.TryGetProperty("transcript", out var transcript)) transcripts.Add(transcript.GetString() ?? string.Empty);
            if (language is null && document.RootElement.TryGetProperty("language_code", out var languageElement)) language = languageElement.GetString();
        }
        return JsonSerializer.Serialize(new { transcript = string.Join("\n", transcripts), language_code = language });
    }

    private static TranscriptionResponse ConvertSarvamTranscriptionResponse(
        string json,
        string model,
        string providerId,
        DateTime timestamp,
        IDictionary<string, string>? headers = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var transcript = root.TryGetProperty("transcript", out var transcriptElement) ? transcriptElement.GetString() ?? string.Empty : string.Empty;
        var language = root.TryGetProperty("language_code", out var languageElement) ? languageElement.GetString() : null;
        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("timestamps", out var timestamps)
            && timestamps.ValueKind == JsonValueKind.Object
            && timestamps.TryGetProperty("words", out var words)
            && timestamps.TryGetProperty("start_time_seconds", out var starts)
            && timestamps.TryGetProperty("end_time_seconds", out var ends))
        {
            var wordList = words.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();
            var startList = starts.EnumerateArray().Select(item => item.GetDouble()).ToList();
            var endList = ends.EnumerateArray().Select(item => item.GetDouble()).ToList();
            for (var index = 0; index < new[] { wordList.Count, startList.Count, endList.Count }.Min(); index++)
                segments.Add(new TranscriptionSegment { Text = wordList[index], StartSecond = (float)startList[index], EndSecond = (float)endList[index] });
        }

        return new TranscriptionResponse
        {
            Text = transcript,
            Language = language,
            Segments = segments,
            ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
            Response = new() { Timestamp = timestamp, Headers = headers, ModelId = model, Body = json }
        };
    }
}
