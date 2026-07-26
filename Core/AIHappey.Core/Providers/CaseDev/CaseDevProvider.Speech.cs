using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.CaseDev;

public partial class CaseDevProvider
{
    private static readonly JsonSerializerOptions CaseDevSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var payload = CopyCaseDevOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        var shortcutVoice = TryGetCaseDevVoiceFromModel(request.Model);
        var voice = !string.IsNullOrWhiteSpace(request.Voice) ? request.Voice.Trim() : shortcutVoice;
        var outputFormat = string.IsNullOrWhiteSpace(request.OutputFormat)
            ? ReadCaseDevString(payload, "output_format") ?? "mp3_44100_128"
            : request.OutputFormat.Trim();

        // Contract fields intentionally win over arbitrary provider options when supplied.
        payload["text"] = request.Text;
        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice_id"] = voice;
        payload["output_format"] = outputFormat;
        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language_code"] = request.Language.Trim();

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/voice/v1/speak")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CaseDevSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CaseDev speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveCaseDevSpeechMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = payload },
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
        // Case.dev exposes a native endpoint, but the gateway's stream contract is byte-delta
        // based and does not require an upstream streaming dependency.
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var streamEvent in response.ToOpenAISpeechStreamEvents())
            yield return streamEvent;
    }

    private static Dictionary<string, object?> CopyCaseDevOptions(JsonElement metadata)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in metadata.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static string? ReadCaseDevString(Dictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
            _ => null
        } : null;

    private string? TryGetCaseDevVoiceFromModel(string model)
    {
        var providerPrefix = GetIdentifier() + "/";
        var normalized = model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model[providerPrefix.Length..]
            : model;
        const string voicePrefix = "case-tts/";
        return normalized.StartsWith(voicePrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[voicePrefix.Length..].Trim()
            : null;
    }

    private static string ResolveCaseDevSpeechMimeType(string outputFormat)
        => outputFormat.Trim().ToLowerInvariant() switch
        {
            var format when format.StartsWith("mp3", StringComparison.Ordinal) => "audio/mpeg",
            var format when format.StartsWith("pcm", StringComparison.Ordinal) => "audio/pcm",
            _ => "application/octet-stream"
        };

}
