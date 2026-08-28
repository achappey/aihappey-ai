using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
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
        if (string.IsNullOrWhiteSpace(request.Voice))
            throw new ArgumentException("Voice is required for Alibaba speech synthesis.", nameof(request));
      
        var input = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice"] = request.Voice,
            ["language_type"] = request.Language
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            input["instructions"] = request.Instructions;

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (property.Name.Equals("optimize_instructions", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("language_type", StringComparison.OrdinalIgnoreCase))
                    input[property.Name] = property.Value.Clone();
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = input
        };
        var body = JsonSerializer.Serialize(payload, AlibabaSpeechJsonOptions);

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/services/aigc/multimodal-generation/generation")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Alibaba speech synthesis failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("output", out var output)
            || !output.TryGetProperty("audio", out var audio))
            throw new InvalidOperationException($"Alibaba speech synthesis returned no audio. Body: {raw}");

        byte[] audioBytes;
        string? audioUrl = null;
        var base64 = audio.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(base64))
        {
            audioBytes = Convert.FromBase64String(base64);
        }
        else
        {
            audioUrl = audio.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new InvalidOperationException("Alibaba speech synthesis returned neither audio data nor an audio URL.");

            using var audioRequest = new HttpRequestMessage(HttpMethod.Get, audioUrl);
            using var audioResponse = await _client.SendAsync(
                audioRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            audioBytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!audioResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Alibaba speech audio download failed ({(int)audioResponse.StatusCode}).");
        }

        var format = ResolveAlibabaSpeechFormat(request.OutputFormat, audioUrl);
        var mimeType = ResolveAlibabaSpeechMimeType(format);
        var warnings = request.Speed.HasValue
            ? new object[] { new { type = "unsupported", feature = "speed" } }
            : [];

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = new SpeechRequest
        {
            Model = options.Model,
            Text = options.Input,
            Voice = options.Voice,
            OutputFormat = options.ResponseFormat,
            Instructions = options.Instructions,
            Speed = options.Speed,
            ProviderOptions = options.AdditionalProperties is { Count: > 0 }
                ? new Dictionary<string, JsonElement>
                {
                    [GetIdentifier()] = JsonSerializer.SerializeToElement(options.AdditionalProperties)
                }
                : null
        };
        var response = await SpeechRequest(request, cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static string ResolveAlibabaSpeechFormat(string? requested, string? url)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim().ToLowerInvariant();
        var extension = Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url);
        return string.IsNullOrWhiteSpace(extension) ? "wav" : extension.TrimStart('.').ToLowerInvariant();
    }

    private static string ResolveAlibabaSpeechMimeType(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "pcm" => "audio/pcm",
        _ => "audio/wav"
    };

    private static readonly JsonSerializerOptions AlibabaSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
