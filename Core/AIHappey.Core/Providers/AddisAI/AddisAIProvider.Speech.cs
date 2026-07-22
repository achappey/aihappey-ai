using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AddisAI;

public partial class AddisAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var language = GetTtsLanguage(request.Model);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat) && !string.Equals(request.OutputFormat, "wav", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "outputFormat", message = "AddisAI returns WAV audio." });

        var payload = new { text = request.Text, language, stream = false };
        ApplyAuthHeader();
        var payloadJson = JsonSerializer.Serialize(payload, AddisJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AddisAI speech generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var audio = GetRequiredString(root, "audio", raw);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = audio, MimeType = "audio/wav", Format = "wav" },
            Warnings = warnings,
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { language, response = root.Clone() }, AddisJson)
            },
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
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
