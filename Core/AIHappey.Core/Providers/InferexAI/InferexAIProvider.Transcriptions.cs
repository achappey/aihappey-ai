using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider
{
    private static byte[] DecodeAudio(object? audio)
    {
        var value = audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio must be a base64 data URL.");
            value = value[(comma + 1)..];
        }
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length == 0) throw new ArgumentException("Audio must not be empty.");
        if (bytes.Length > 25 * 1024 * 1024) throw new ArgumentException("InferexAI audio uploads are limited to 25 MB.");
        return bytes;
    }

    private static double PollSeconds(Dictionary<string, object?> options, string name, double fallback)
    {
        if (!options.Remove(name, out var raw)) return fallback;
        var seconds = JsonSerializer.SerializeToElement(raw).GetDouble();
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
            throw new ArgumentException($"{name} must be positive and at most 86400 seconds.");
        return seconds;
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var bytes = DecodeAudio(request.Audio);
        var options = Options(request.ProviderOptions);
        var asyncJob = options.Remove("async_job", out var job) && JsonSerializer.SerializeToElement(job).GetBoolean();
        var interval = PollSeconds(options, "poll_interval_seconds", 3);
        var timeout = PollSeconds(options, "poll_timeout_seconds", 1800);
        options.Remove("model");
        if (options.Remove("language", out var language) && !options.ContainsKey("language_code"))
            options["language_code"] = language;
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(request.MediaType);
        var extension = request.MediaType.ToLowerInvariant() switch
        {
            "audio/aiff" or "audio/x-aiff" => ".aiff",
            "audio/amr" => ".amr", "audio/x-ms-wma" => ".wma",
            "video/mp4" => ".mp4", _ => request.MediaType.GetAudioExtension()
        };
        form.Add(file, "file", "audio" + extension);
        form.Add(new StringContent(NativeModel(request.Model)), "model");
        foreach (var (name, value) in options)
        {
            if (name == "file") throw new ArgumentException("Provider options cannot replace the audio file.");
            var element = JsonSerializer.SerializeToElement(value);
            if (element.ValueKind == JsonValueKind.Null) continue;
            form.Add(new StringContent(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText()), name);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var path = asyncJob ? "v1/audio/transcriptions/jobs" : "v1/audio/transcriptions";
        using var response = await _client.PostAsync(path, form, deadline.Token);
        var root = await ReadJson(response, deadline.Token);
        var submitted = root.Clone();
        var headers = response.GetHeaders();
        if (asyncJob)
        {
            var id = root.GetProperty("id").GetString();
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            while (true)
            {
                var status = String(root, "status");
                if (status == "completed") break;
                if (status == "failed") throw new InvalidOperationException($"InferexAI transcription job failed: {root}");
                if (status is not ("queued" or "processing"))
                    throw new InvalidOperationException($"InferexAI returned unknown transcription job status: {root}");
                await Task.Delay(TimeSpan.FromSeconds(interval), deadline.Token);
                using var poll = await _client.GetAsync("v1/audio/transcriptions/jobs/" + Uri.EscapeDataString(id), deadline.Token);
                root = await ReadJson(poll, deadline.Token);
                headers = poll.GetHeaders();
            }
        }
        deadline.Token.ThrowIfCancellationRequested();
        return new()
        {
            Text = root.GetProperty("text").GetString() ?? throw new InvalidOperationException("InferexAI returned a null transcript."),
            Language = String(root, "language"), DurationInSeconds = Number(root, "duration"),
            Segments = Segments(root),
            ProviderMetadata = asyncJob ? Metadata(JsonSerializer.SerializeToElement(new { submitted, result = root })) : Metadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{NativeModel(request.Model)}", Headers = headers, Body = root.GetRawText() }
        };
    }

    private static TranscriptionSegment[] Segments(JsonElement root)
        => root.TryGetProperty("segments", out var segments) ? segments.EnumerateArray().Select(s => new TranscriptionSegment
        {
            Text = s.GetProperty("text").GetString()!,
            StartSecond = s.GetProperty("start").GetDouble(), EndSecond = s.GetProperty("end").GetDouble()
        }).ToArray() : [];

    private async Task<TranscriptionRequest> ToTranscription(OpenAITranscriptionRequest options, CancellationToken ct)
    {
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        if (responseFormat == "diarized_json")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
            if (options.File is null || options.File.Length == 0) throw new ArgumentException("file is required.");
            if (options.ChunkingStrategy is not null || options.Include?.Length > 0 ||
                options.KnownSpeakerNames?.Length > 0 || options.KnownSpeakerReferences?.Length > 0)
                throw new NotSupportedException("InferexAI does not document chunking_strategy, include or known-speaker fields.");
        }
        else
            options.ValidateOpenAITranscriptionRequest(ProviderId);
        if (options.Prompt is not null || options.Temperature is not null || options.TimestampGranularities?.Length > 0)
            throw new NotSupportedException("InferexAI does not document prompt, temperature or timestamp_granularities for transcription. Pass model-specific fields in provider options.");
        var request = await options.ToTranscriptionRequest(NativeModel(options.Model), ProviderId, ct);
        var native = new Dictionary<string, object?>();
        if (options.AdditionalProperties?.TryGetValue("providerOptions", out var scoped) == true)
            native = Options(scoped.Deserialize<Dictionary<string, JsonElement>>());
        else if (options.AdditionalProperties?.TryGetValue(ProviderId, out var value) == true)
            native = Options(Metadata(value));
        else if (options.AdditionalProperties is not null)
            foreach (var field in options.AdditionalProperties) native[field.Key] = field.Value;
        if (options.Language is not null) native["language_code"] = options.Language;
        if (responseFormat == "diarized_json")
        {
            native["async_job"] = true;
            native["diarize"] = true;
        }
        request.ProviderOptions = Metadata(JsonSerializer.SerializeToElement(native));
        return request;
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        var response = await TranscriptionRequest(await ToTranscription(options, cancellationToken), cancellationToken);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        using var document = JsonDocument.Parse(response.Response.Body!.ToString()!);
        var root = document.RootElement;
        if (format == "diarized_json")
            return new OpenAITranscriptionDiarizedResponse
            {
                Text = response.Text,
                Duration = response.DurationInSeconds ?? throw new InvalidOperationException("InferexAI job omitted duration."),
                Segments = root.GetProperty("segments").EnumerateArray().Select((s, i) => new OpenAITranscriptionTextSegment
                {
                    Id = String(s, "id") ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text = s.GetProperty("text").GetString()!, Speaker = s.GetProperty("speaker").GetString()!,
                    Start = s.GetProperty("start").GetDouble(), End = s.GetProperty("end").GetDouble()
                }).ToArray(),
                Usage = root.TryGetProperty("usage", out var diarizedUsage) ? diarizedUsage.Deserialize<OpenAITranscriptionUsage>() : null
            };
        var result = response.ToOpenAITranscriptionResponse(format);
        if (result is OpenAITranscriptionResponse json && root.TryGetProperty("usage", out var usage))
            json.Usage = usage.Deserialize<OpenAITranscriptionUsage>();
        if (result is OpenAITranscriptionVerboseResponse verbose)
        {
            if (root.TryGetProperty("words", out var words)) verbose.Words = words.Deserialize<OpenAITranscriptionWord[]>();
            if (root.TryGetProperty("usage", out var verboseUsage))
                verbose.Usage = verboseUsage.Deserialize<OpenAITranscriptionUsage>();
        }
        return result;
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Uploaded-file transcription has no documented SSE endpoint; this is the existing buffered bridge.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Text.Length > 0) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}
