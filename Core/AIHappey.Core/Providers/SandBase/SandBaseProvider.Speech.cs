using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var payload = CreateRunPayload(request.ProviderOptions, request.Model);
        if (!string.IsNullOrWhiteSpace(request.Text)) payload["prompt"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["output_format"] = request.OutputFormat;
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["language_code"] = request.Language;
        if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["style_instructions"] = request.Instructions;
        if (request.Speed.HasValue) payload["speed"] = request.Speed.Value;

        var run = await AwaitRunAsync(await SubmitRunAsync(payload, cancellationToken), cancellationToken);
        var url = RunOutputs(run.Root).Select(output => FindMedia(output, "audio"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidOperationException("SandBase completed the audio run without a usable audio output.");
        var fallback = request.OutputFormat?.ToLowerInvariant() switch
        {
            "wav" => "audio/wav", "ogg" or "ogg_opus" => "audio/ogg", "flac" => "audio/flac", _ => "audio/mpeg"
        };
        var (bytes, mime) = await ReadRunMediaAsync(url, fallback, cancellationToken);
        var format = mime switch { "audio/wav" => "wav", "audio/ogg" => "ogg", "audio/flac" => "flac", _ => "mp3" };
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(bytes), MimeType = mime, Format = format },
            ProviderMetadata = RunMetadata(run.Root),
            Request = new() { Body = payload },
            Response = new() { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Headers = run.Headers, Body = run.Root }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = new SpeechRequest
        {
            Model = options.Model, Text = options.Input, Voice = options.Voice,
            OutputFormat = options.ResponseFormat, Instructions = options.Instructions, Speed = options.Speed
        };
        if (options.AdditionalProperties?.Count > 0)
            request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        var result = await SpeechRequest(request, cancellationToken);
        return (Convert.FromBase64String(result.Audio.Base64), result.Audio.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }
}
