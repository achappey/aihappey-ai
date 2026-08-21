using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SunbirdAI;

public partial class SunbirdAIProvider
{
    private const string TranscriptionsEndpoint = "tasks/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var encodedAudio = request.Audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(encodedAudio))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audio = DecodeSunbirdAudio(encodedAudio, nameof(request));
        var options = GetSunbirdObject(request.ProviderOptions);
        var result = await SendSunbirdTranscriptionAsync(
            audio,
            request.MediaType,
            "audio" + GetSunbirdAudioExtension(request.MediaType),
            options,
            cancellationToken);

        return ToSunbirdTranscriptionResponse(result, request.Model, request.MediaType, audio.LongLength, options);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        if (options.File is null || options.File.Length == 0)
            throw new ArgumentException("'file' is a required field", nameof(options));

        await using var input = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken);

        var rawOptions = SunbirdJsonObject(options.AdditionalProperties);
        SetSunbirdValue(rawOptions, "language", options.Language);
        SetSunbirdValue(rawOptions, "prompt", options.Prompt);
        SetSunbirdValue(rawOptions, "response_format", options.ResponseFormat);
        if (options.Temperature is not null)
            rawOptions["temperature"] = options.Temperature.Value;
        if (options.TimestampGranularities?.Length > 0)
            rawOptions["timestamp_granularities"] = options.TimestampGranularities;
        if (options.Include?.Length > 0)
            rawOptions["include"] = options.Include;
        if (options.ChunkingStrategy is not null)
            rawOptions["chunking_strategy"] = options.ChunkingStrategy;
        if (options.KnownSpeakerNames?.Length > 0)
            rawOptions["known_speaker_names"] = options.KnownSpeakerNames;
        if (options.KnownSpeakerReferences?.Length > 0)
            rawOptions["known_speaker_references"] = options.KnownSpeakerReferences;

        var mediaType = string.IsNullOrWhiteSpace(options.File.ContentType)
            ? GetSunbirdMediaType(options.File.FileName)
            : options.File.ContentType;
        var result = await SendSunbirdTranscriptionAsync(
            memory.ToArray(),
            mediaType,
            string.IsNullOrWhiteSpace(options.File.FileName) ? "audio" + GetSunbirdAudioExtension(mediaType) : options.File.FileName,
            rawOptions,
            cancellationToken);

        var response = ToSunbirdTranscriptionResponse(
            result,
            options.Model,
            mediaType,
            memory.Length,
            rawOptions);
        return response.ToOpenAITranscriptionResponse(options.ResolveOpenAITranscriptionResponseFormat());
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<(JsonElement Root, Dictionary<string, string> Headers, DateTime Timestamp)>
        SendSunbirdTranscriptionAsync(
            byte[] audio,
            string mediaType,
            string fileName,
            Dictionary<string, object?> options,
            CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "audio", fileName);
        AddSunbirdMultipartOptions(form, options);

        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        using var response = await _client.PostAsync(TranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SunbirdAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return (document.RootElement.Clone(), response.GetHeaders(), timestamp);
    }

    private TranscriptionResponse ToSunbirdTranscriptionResponse(
        (JsonElement Root, Dictionary<string, string> Headers, DateTime Timestamp) result,
        string model,
        string mediaType,
        long audioLength,
        Dictionary<string, object?> options)
    {
        var root = result.Root;
        var text = SunbirdString(root, "audio_transcription") ?? SunbirdString(root, "text")
            ?? throw new InvalidOperationException("SunbirdAI transcription response did not include audio_transcription.");
        var duration = SunbirdNumber(root, "duration_seconds")
            ?? SunbirdNumber(root, "original_duration_minutes") * 60d;

        return new TranscriptionResponse
        {
            Text = text,
            Language = SunbirdString(root, "language") ?? SunbirdOptionString(options, "language"),
            DurationInSeconds = duration is null ? null : (float)duration.Value,
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(new
                {
                    file = new { mediaType, byteLength = audioLength },
                    options
                })
            },
            Response = new ResponseData
            {
                Timestamp = result.Timestamp,
                Headers = result.Headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static void AddSunbirdMultipartOptions(MultipartFormDataContent form, Dictionary<string, object?> options)
    {
        foreach (var (name, value) in options)
        {
            if (name.Equals("audio", StringComparison.OrdinalIgnoreCase)
                || name.Equals("file", StringComparison.OrdinalIgnoreCase)
                || value is null)
                continue;

            var text = value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement element => element.GetRawText(),
                string stringValue => stringValue,
                bool boolean => boolean ? "true" : "false",
                _ => JsonSerializer.Serialize(value)
            };
            if (text is not null)
                form.Add(new StringContent(text, Encoding.UTF8), name);
        }
    }

    private static byte[] DecodeSunbirdAudio(string encodedAudio, string parameterName)
    {
        encodedAudio = encodedAudio.Trim();
        if (encodedAudio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = encodedAudio.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Audio data URL is invalid.", parameterName);
            encodedAudio = encodedAudio[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(encodedAudio);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must contain valid base64 data.", parameterName, exception);
        }
    }

    private static string? SunbirdString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? SunbirdNumber(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? SunbirdOptionString(Dictionary<string, object?> options, string name)
        => options.TryGetValue(name, out var value) ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        } : null;

    private static string GetSunbirdAudioExtension(string mediaType)
        => mediaType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            _ => ".audio"
        };

    private static string GetSunbirdMediaType(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" or ".mp4" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            _ => "application/octet-stream"
        };
}
