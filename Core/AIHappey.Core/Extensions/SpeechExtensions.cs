using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Extensions;

public static class SpeechExtensions
{
    public static SpeechRequest ToSpeechRequest(
       this AudioSpeechRequest request)
        => new()
        {
            Model = request.Model,
            Voice = request.Voice,
            Speed = request.Speed,
            OutputFormat = request.ResponseFormat,
            Instructions = request.Instructions,
            Text = request.Input
        };

    public static (byte[] Audio, string MimeType) ToOpenAISpeechAudio(
        this SpeechResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.Audio);

        var base64 = response.Audio.Base64;
        var mimeType = response.Audio.MimeType;

        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Speech response did not include audio data.");

        var commaIndex = base64.IndexOf(',');
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                var semicolonIndex = base64.IndexOf(';');
                if (semicolonIndex > "data:".Length)
                    mimeType = base64["data:".Length..semicolonIndex];
            }

            base64 = base64[(commaIndex + 1)..];
        }

        return (Convert.FromBase64String(base64), string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
    }

    public static IEnumerable<IAudioSpeechStreamEvent> ToOpenAISpeechStreamEvents(
        this SpeechResponse response)
    {
        var (audio, _) = response.ToOpenAISpeechAudio();

        yield return new AudioSpeechStreamDelta
        {
            Audio = Convert.ToBase64String(audio)
        };

        yield return new AudioSpeechStreamDone();
    }

    public static async Task<TranscriptionRequest> ToTranscriptionRequest(
       this AudioTranscriptionRequest request,
       string model,
       string providerIdentifier,
       CancellationToken cancellationToken = default)
    {
        if (request.File == null || request.File.Length == 0)
            throw new ArgumentException("'file' is a required field", nameof(request));

        await using var stream = request.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        return new()
        {
            Model = model,
            Audio = Convert.ToBase64String(memory.ToArray()),
            MediaType = ResolveMediaType(request.File),
            ProviderOptions = BuildTranscriptionProviderOptions(request, providerIdentifier)
        };
    }

    public static AudioTranscriptionRequest ToAudioTranscriptionRequest(this IFormCollection form)
        => new()
        {
            File = form.Files.GetFile("file")!,
            Model = ReadFormString(form, "model")!,
            Language = ReadFormString(form, "language"),
            Prompt = ReadFormString(form, "prompt"),
            ResponseFormat = ReadFormString(form, "response_format"),
            Temperature = ReadFormFloat(form, "temperature"),
            TimestampGranularities = ReadFormArray(form, "timestamp_granularities", "timestamp_granularities[]"),
            Stream = ReadFormBool(form, "stream"),
            Include = ReadFormArray(form, "include", "include[]"),
            ChunkingStrategy = ReadFormString(form, "chunking_strategy"),
            KnownSpeakerNames = ReadFormArray(form, "known_speaker_names", "known_speaker_names[]"),
            KnownSpeakerReferences = ReadFormArray(form, "known_speaker_references", "known_speaker_references[]")
        };

    public static object ToOpenAITranscriptionResponse(
        this TranscriptionResponse response,
        string responseFormat)
        => responseFormat switch
        {
            "verbose_json" => new
            {
                task = "transcribe",
                language = response.Language,
                duration = response.DurationInSeconds,
                text = response.Text,
                segments = response.Segments.Select((segment, index) => new
                {
                    id = index,
                    seek = 0,
                    start = segment.StartSecond,
                    end = segment.EndSecond,
                    text = segment.Text,
                    tokens = Array.Empty<int>(),
                    temperature = 0,
                    avg_logprob = 0,
                    compression_ratio = 0,
                    no_speech_prob = 0
                }),
                words = Array.Empty<object>()
            },
            _ => new
            {
                text = response.Text
            }
        };

    public static string ResolveOpenAITranscriptionResponseFormat(this AudioTranscriptionRequest request)
        => string.IsNullOrWhiteSpace(request.ResponseFormat)
            ? "json"
            : request.ResponseFormat.Trim().ToLowerInvariant();

    public static void ValidateOpenAITranscriptionRequest(this AudioTranscriptionRequest request)
    {
        if (request == null)
            throw new ArgumentException("Request is required.");

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("'model' is a required field");

        if (request.File == null || request.File.Length == 0)
            throw new ArgumentException("'file' is a required field");

        var responseFormat = request.ResolveOpenAITranscriptionResponseFormat();
        if (responseFormat is "diarized_json" or "srt" or "vtt")
            throw new NotSupportedException($"OpenAI transcription response_format '{responseFormat}' is not supported by this compatibility endpoint yet.");

        if (responseFormat is not "json" and not "text" and not "verbose_json")
            throw new NotSupportedException($"OpenAI transcription response_format '{responseFormat}' is not supported.");

        if (!string.IsNullOrWhiteSpace(request.ChunkingStrategy))
            throw new NotSupportedException("OpenAI transcription 'chunking_strategy' is not supported by this compatibility endpoint yet.");

        if (request.Include?.Any() == true)
            throw new NotSupportedException("OpenAI transcription 'include[]' is not supported by this compatibility endpoint yet.");

        if (request.KnownSpeakerNames?.Any() == true || request.KnownSpeakerReferences?.Any() == true)
            throw new NotSupportedException("OpenAI transcription known speaker fields are not supported by this compatibility endpoint yet.");
    }

    private static Dictionary<string, JsonElement>? BuildTranscriptionProviderOptions(
        AudioTranscriptionRequest request,
        string providerIdentifier)
    {
        var options = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            options["language"] = request.Language;

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            options["prompt"] = request.Prompt;

        if (request.Temperature is not null)
            options["temperature"] = request.Temperature;

        if (request.TimestampGranularities?.Any() == true)
            options["timestamp_granularities"] = request.TimestampGranularities;

        return options.Count == 0
            ? null
            : new Dictionary<string, JsonElement>
            {
                [providerIdentifier] = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web)
            };
    }

    private static string? ReadFormString(IFormCollection form, string name)
        => form.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.FirstOrDefault())
            ? value.FirstOrDefault()
            : null;

    private static string[]? ReadFormArray(IFormCollection form, params string[] names)
    {
        var values = names
            .Where(form.ContainsKey)
            .SelectMany(name => form[name])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    private static float? ReadFormFloat(IFormCollection form, string name)
        => float.TryParse(
            ReadFormString(form, name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static bool? ReadFormBool(IFormCollection form, string name)
        => ReadFormString(form, name)?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };

    private static string ResolveMediaType(Microsoft.AspNetCore.Http.IFormFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType))
            return file.ContentType;

        return Path.GetExtension(file.FileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".mpeg" => "audio/mpeg",
            ".mpga" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".webm" => "audio/webm",
            _ => "application/octet-stream"
        };
    }

}
