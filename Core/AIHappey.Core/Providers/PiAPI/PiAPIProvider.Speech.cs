using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PiAPI;

public partial class PiAPIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await CreateSpeechTaskAsync(
            request.Model,
            request.Text,
            request.Voice,
            request.OutputFormat,
            request.Speed,
            request.Instructions,
            request.ProviderOptions,
            cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = result.Audio.Base64,
                MimeType = result.Audio.MimeType,
                Format = ResolveSpeechFormat(request.OutputFormat, result.Audio.MimeType)
            },
            ProviderMetadata = CreateMediaProviderMetadata(result.Create, result.Task),
            Request = new SpeechRequestItem { Body = result.Input },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Task.Root
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = await CreateSpeechTaskAsync(
            options.Model,
            options.Input,
            options.Voice,
            options.ResponseFormat,
            options.Speed,
            options.Instructions,
            null,
            cancellationToken);

        return (Convert.FromBase64String(result.Audio.Base64), result.Audio.MimeType);
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

    private async Task<(PiApiTaskResult Create, PiApiTaskResult Task, (string Base64, string MimeType) Audio, Dictionary<string, object?> Input)> CreateSpeechTaskAsync(
        string model,
        string text,
        string? voice,
        string? outputFormat,
        float? speed,
        string? instructions,
        Dictionary<string, System.Text.Json.JsonElement>? providerOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Speech input is required.", nameof(text));

        var input = new Dictionary<string, object?>
        {
            ["gen_text"] = text,
            ["voice"] = voice,
            ["output_format"] = outputFormat,
            ["speed"] = speed,
            ["instructions"] = instructions
        };
        var task = await CreateAndWaitForMediaTaskAsync(model, "zero-shot", input, providerOptions, cancellationToken);
        var output = GetOutputValues(task.Result.Root, "audio_url", "audio", "audio_urls").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("PiAPI speech task completed without generated audio.");

        var audio = await DownloadMediaAsync(output, ResolveSpeechMimeType(outputFormat), cancellationToken);
        return (task.Create, task.Result, audio, input);
    }

    private static string ResolveSpeechFormat(string? format, string mimeType)
        => string.IsNullOrWhiteSpace(format)
            ? mimeType switch
            {
                "audio/wav" or "audio/x-wav" => "wav",
                "audio/opus" => "opus",
                "audio/flac" => "flac",
                "audio/aac" => "aac",
                _ => "mp3"
            }
            : format;

    private static string ResolveSpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" or "wave" => "audio/wav",
            "opus" => "audio/opus",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };
}
