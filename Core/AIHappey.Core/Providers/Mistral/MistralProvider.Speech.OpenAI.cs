using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
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
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        ApplyAuthHeader();

        var request = options.ToSpeechRequest();
        var warnings = new List<object>();
        var selection = await ResolveSpeechSelectionAsync(request, null, warnings, cancellationToken);
        var responseFormat = NormalizeSpeechResponseFormat(options.ResponseFormat);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = options.Input,
            ["model"] = selection.ModelId,
            ["stream"] = true
        };

        if (!string.IsNullOrWhiteSpace(selection.VoiceId))
            payload["voice_id"] = selection.VoiceId;
        if (!string.IsNullOrWhiteSpace(responseFormat))
            payload["response_format"] = responseFormat;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{GetName()} streaming speech request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                continue;

            using var document = JsonDocument.Parse(data);
            var audio = document.RootElement.TryGetString("audio_data");
            if (!string.IsNullOrWhiteSpace(audio))
                yield return new AudioSpeechStreamDelta { Audio = audio };
        }

        yield return new AudioSpeechStreamDone();
    }
}
