using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));

        var payload = GetHiNowOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        SetHiNow(payload, "voice", request.Voice);
        SetHiNow(payload, "speed", request.Speed);
        SetHiNow(payload, "output_format", request.OutputFormat);
        payload["async"] = false;

        var result = await SendHiNowJsonAsync(HttpMethod.Post, "v1/audio/speech", payload, "speech generation", cancellationToken);
        var data = GetHiNowData(result.Root);
        var url = GetHiNowUrls(data).FirstOrDefault()
            ?? throw new InvalidOperationException("HiNow speech response did not contain an audio URL.");
        var downloaded = await DownloadHiNowMediaAsync(url, "audio/mpeg", cancellationToken);
        var format = Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).TrimStart('.');
        if (string.IsNullOrWhiteSpace(format)) format = request.OutputFormat ?? "mp3";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(downloaded.Bytes), MimeType = downloaded.MediaType, Format = format },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions) && string.IsNullOrWhiteSpace(request.Language)
                ? [] : [new { type = "unsupported", feature = "instructions/language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow, Headers = result.Headers,
                ModelId = (GetHiNowString(data, "model") ?? request.Model).ToModelId(GetIdentifier()), Body = result.Root
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }
}
