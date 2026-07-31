using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MoleAPI;

public partial class MoleAPIProvider
{
    private static readonly JsonSerializerOptions SpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });
        var payload = new
        {
            model = NormalizeProviderModelId(request.Model),
            input = request.Text,
            voice = request.Voice ?? "alloy",
            response_format = format,
            speed = request.Speed
        };

        using var response = await _client.PostAsync("v1/audio/speech",
            new StringContent(JsonSerializer.Serialize(payload, SpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MoleAPI speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveSpeechMimeType(format),
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(response.Audio) };
        yield return new AudioSpeechStreamDone { };
    }

    private static string ResolveSpeechMimeType(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg", "wav" => "audio/wav", "aac" => "audio/aac", "opus" => "audio/opus",
        "flac" => "audio/flac", "pcm" => "audio/pcm", _ => "application/octet-stream"
    };
}
