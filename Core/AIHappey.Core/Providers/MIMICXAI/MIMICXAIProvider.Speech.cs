using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (audio, mimeType, raw) = await SynthesizeAsync(request.Model, request.Text, request.Language, cancellationToken);
        var warnings = new List<object>();
        if (request.Voice is not null || request.OutputFormat is not null || request.Instructions is not null || request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "voice/outputFormat/instructions/speed" });
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(audio), MimeType = mimeType, Format = mimeType.Split('/').Last() },
            Warnings = warnings,
            ProviderMetadata = raw is null ? null : GetIdentifier().CreatePrimitiveProviderMetadata(raw.Value),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()), Body = raw }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = await SynthesizeAsync(options.Model, options.Input, null, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType, JsonElement? Raw)> SynthesizeAsync(string? model, string text, string? language,
        CancellationToken cancellationToken)
    {
        if (NormalizeAgentModel(model) != "Nova") throw new NotSupportedException("MIMICXAI speech synthesis requires Nova.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/synthesize")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text, language }, AgentJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MIMICXAI speech synthesis failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) return (bytes, mediaType, null);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement.Clone();
        ThrowAgentPayloadError(root);
        var base64 = GetString(root, "audio_b64") ?? GetString(root, "audio_base64") ?? GetString(root, "audio");
        if (string.IsNullOrWhiteSpace(base64)) throw new InvalidOperationException("MIMICXAI synthesis response did not contain audio.");
        return (Convert.FromBase64String(StripDataUrl(base64)), GetString(root, "mime_type") ?? "audio/mpeg", root);
    }
}
