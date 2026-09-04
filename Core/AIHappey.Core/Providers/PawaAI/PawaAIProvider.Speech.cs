using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
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

        var payload = CopyPawaOptions(GetPawaOptions(request.ProviderOptions));
        payload["text"] = request.Text;
        payload["model"] = NormalizePawaModelId(request.Model);
        payload["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? "ame" : request.Voice;

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(request.OutputFormat, "mp3", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "outputFormat", message = "Pawa AI returns MPEG audio." });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/voice/text-to-speech")
        {
            Content = new StringContent(payload.ToJsonString(PawaJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            EnsurePawaSuccess(response, Encoding.UTF8.GetString(audio), "speech request");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mediaType,
                Format = "mp3"
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                contentType = mediaType,
                contentLength = audio.LongLength
            }),
            Request = new SpeechRequestItem { Body = JsonSerializer.SerializeToElement(payload, PawaJson) },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }
}
