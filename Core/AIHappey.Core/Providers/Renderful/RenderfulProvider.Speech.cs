using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        List<object> warnings = [];
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = CreateRenderfulPayload(request.ProviderOptions, new Dictionary<string, object?>
        {
            ["type"] = "text-to-audio",
            ["model"] = request.Model,
            ["prompt"] = request.Text,
            ["voice_id"] = request.Voice,
            ["audio_format"] = request.OutputFormat,
            ["speed"] = request.Speed
        });

        var generation = await CreateGenerationAsync(payload, cancellationToken);
        var output = generation.Outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("Renderful speech generation completed without an audio output.");
        var (bytes, mimeType) = await DownloadOutputAsync(output, "audio/mpeg", cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = GetAudioFormat(mimeType, request.OutputFormat)
            },
            Warnings = warnings,
            ProviderMetadata = CreateRenderfulMetadata(generation.Root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

   public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static string GetAudioFormat(string mimeType, string? requestedFormat)
    {
        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat.Trim().ToLowerInvariant();

        return mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            _ => "mp3"
        };
    }
}
