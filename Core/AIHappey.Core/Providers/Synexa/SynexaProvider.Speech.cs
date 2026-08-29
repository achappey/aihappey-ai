using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
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

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = request.Text,
            ["voice"] = request.Voice,
            ["speed"] = request.Speed,
            ["output_format"] = request.OutputFormat,
            ["format"] = request.OutputFormat,
            ["language"] = request.Language,
            ["instructions"] = request.Instructions
        };
        MergeSynexaInputMetadata(input, metadata, "prompt", "voice", "speed", "output_format", "format", "language", "instructions");

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, GetSynexaWaitOptions(metadata), cancellationToken);
        var output = ExtractStringOutputs(completed.Output).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Synexa speech prediction returned no audio output.");

        var fallbackMimeType = ResolveSynexaAudioMimeType(request.OutputFormat);
        var (bytes, mimeType) = await ResolveOutputBytesAsync(output, fallbackMimeType, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = ResolveSynexaAudioFormat(mimeType, request.OutputFormat)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(CreateSynexaPredictionMetadata(completed)),
            Warnings = [],
            Request = new SpeechRequestItem { Body = input },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = completed.Raw.Clone()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model) || string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Model and input are required.", nameof(options));

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        if (audio.Length > 0)
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static string ResolveSynexaAudioMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };

    private static string ResolveSynexaAudioFormat(string mimeType, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;
        if (mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (mimeType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return "ogg";
        if (mimeType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        return "mp3";
    }
}
