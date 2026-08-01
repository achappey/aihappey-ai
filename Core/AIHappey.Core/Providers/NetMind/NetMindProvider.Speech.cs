using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NetMind;

public partial class NetMindProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        using var form = new MultipartFormDataContent();
        AddNetMindMetadata(form, metadata);
        Add(form, "model", request.Model); Add(form, "input", request.Text); Add(form, "voice", request.Voice);
        Add(form, "response_format", request.OutputFormat); Add(form, "speed", request.Speed?.ToString(CultureInfo.InvariantCulture));
        Add(form, "instructions", request.Instructions);
        using var response = await _client.PostAsync("audio/speech", form, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NetMind speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        var mime = response.Content.Headers.ContentType?.MediaType ?? NetMindSpeechMime(request.OutputFormat);
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = mime, Format = request.OutputFormat ?? "mp3" },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { response = Convert.ToBase64String(audio), contentType = mime }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    { var result = await SpeechRequest(options.ToSpeechRequest(), cancellationToken); return result.ToOpenAISpeechAudio(); }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var e in result.ToOpenAISpeechStreamEvents()) { cancellationToken.ThrowIfCancellationRequested(); yield return e; }
    }

    private static string NetMindSpeechMime(string? format) => format?.ToLowerInvariant() switch
    { "wav" => "audio/wav", "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac", "pcm" => "audio/pcm", _ => "audio/mpeg" };
}
