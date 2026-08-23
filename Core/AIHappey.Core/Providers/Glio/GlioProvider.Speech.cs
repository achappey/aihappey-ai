using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Glio;

public partial class GlioProvider
{
    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyGlioRootOptions(options);
        var parameters = GetGlioParams(payload);
        var isSuno = request.Model.StartsWith("suno", StringComparison.OrdinalIgnoreCase);
        List<object> warnings = [];

        payload["model"] = request.Model;
        payload["action"] = "generate";
        parameters["prompt"] = request.Text;

        if (!isSuno)
        {
            SetGlioValue(parameters, "voice", request.Voice);
            SetGlioValue(parameters, "speed", request.Speed);
        }
        else
        {
            AddGlioSpeechWarning(warnings, request.Voice, "voice");
            AddGlioSpeechWarning(warnings, request.Speed, "speed");
        }

        AddGlioSpeechWarning(warnings, request.OutputFormat, "outputFormat");
        AddGlioSpeechWarning(warnings, request.Instructions, "instructions");
        AddGlioSpeechWarning(warnings, request.Language, "language");

        var job = await RunGlioJobAsync(payload, cancellationToken);
        var outputUrl = job.Urls[0];
        var fallbackMimeType = GuessGlioAudioMediaType(outputUrl, request.OutputFormat);
        var media = await DownloadGlioMediaAsync(outputUrl, fallbackMimeType, cancellationToken);
        var deletion = await DeleteGlioJobAsync(job.JobId, cancellationToken);
        job = job with { Delete = deletion };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(media.Bytes),
                MimeType = media.MediaType,
                Format = ResolveGlioAudioFormat(media.MediaType, outputUrl, request.OutputFormat)
            },
            Warnings = warnings,
            ProviderMetadata = CreateGlioJobMetadata(job),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = job.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = job.Final
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is a required field", nameof(options));

        Dictionary<string, JsonElement>? providerOptions = null;
        if (options.AdditionalProperties is { Count: > 0 })
        {
            providerOptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>
                    {
                        ["params"] = options.AdditionalProperties
                    },
                    GlioJsonOptions)
            };
        }

        var response = await SpeechRequest(new SpeechRequest
        {
            Model = options.Model,
            Text = options.Input,
            Voice = options.Voice,
            OutputFormat = options.ResponseFormat,
            Instructions = options.Instructions,
            Speed = options.Speed,
            ProviderOptions = providerOptions
        }, cancellationToken);

        return (Convert.FromBase64String(response.Audio.Base64), response.Audio.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static void AddGlioSpeechWarning(List<object> warnings, object? value, string feature)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            warnings.Add(new { type = "unsupported", feature });
    }

    private static string GuessGlioAudioMediaType(string url, string? requestedFormat)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".m4a" => "audio/mp4",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".pcm" => "audio/pcm",
            ".mp3" => "audio/mpeg",
            _ => NormalizeGlioAudioFormat(requestedFormat) switch
            {
                "wav" => "audio/wav",
                "flac" => "audio/flac",
                "aac" => "audio/aac",
                "m4a" => "audio/mp4",
                "ogg" => "audio/ogg",
                "opus" => "audio/opus",
                "pcm" => "audio/pcm",
                _ => "audio/mpeg"
            }
        };
    }

    private static string ResolveGlioAudioFormat(string mimeType, string url, string? requestedFormat)
    {
        var normalized = NormalizeGlioAudioFormat(requestedFormat);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" or "audio/x-m4a" => "m4a",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/pcm" => "pcm",
            "audio/mpeg" or "audio/mp3" => "mp3",
            _ => NormalizeGlioAudioFormat(Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url)) ?? "mp3"
        };
    }

    private static string? NormalizeGlioAudioFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var normalized = format.Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "mpeg" or "audio/mpeg" or "audio/mp3" => "mp3",
            "wave" or "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "mp4" or "audio/mp4" or "audio/x-m4a" => "m4a",
            "oga" or "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/pcm" => "pcm",
            _ => normalized
        };
    }
}
