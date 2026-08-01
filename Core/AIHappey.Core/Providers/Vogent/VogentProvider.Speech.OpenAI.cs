using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vogent;

public partial class VogentProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        var request = ToVogentSpeechRequest(options);
        var response = await SpeechRequest(request, cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = ToVogentSpeechRequest(options);
        ApplyAuthHeader();
        var prepared = PrepareSpeechRequest(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, prepared.Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(prepared.Payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.ParseAdd(ResolveSpeechMimeType(ResolveSpeechFormat(prepared.Format), null));

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        yield return new AudioSpeechStreamDone();
    }

    private SpeechRequest ToVogentSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var request = options.ToSpeechRequest();
        var model = ParseSpeechModel(options.Model);
        if (!model.IsMultispeaker)
            return request;

        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("Voice is required for Vogent multispeaker OpenAI speech requests.", nameof(options));

        request.ProviderOptions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                lines = new[]
                {
                    new { text = options.Input, voiceId = options.Voice.Trim() }
                }
            }, JsonSerializerOptions.Web)
        };

        return request;
    }
}
