using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.PaxaLabs;

public partial class PaxaLabsProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = ResolveSpeechSelection(request.Model, request.Voice);
        var format = ResolveSpeechFormat(request.OutputFormat);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = selection.Model,
            ["input"] = request.Text,
            ["voice"] = selection.Voice,
            ["response_format"] = format,
            ["stream"] = false,
            ["speed"] = request.Speed
        };
        var result = await SendSpeechAsync(payload, format, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(result.Audio), MimeType = result.MimeType, Format = format },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions) ? [] : [new { type = "unsupported", feature = "instructions" }],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { model = selection.Model, voice = selection.Voice, result.Headers })
            },
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selection = ResolveSpeechSelection(options.Model, options.Voice);
        var format = ResolveSpeechFormat(options.ResponseFormat);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = selection.Model, ["input"] = options.Input, ["voice"] = selection.Voice,
            ["response_format"] = format, ["stream"] = false, ["speed"] = options.Speed
        };
        var result = await SendSpeechAsync(payload, format, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var selection = ResolveSpeechSelection(options.Model, options.Voice);
        var format = ResolveSpeechFormat(options.ResponseFormat);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = selection.Model, ["input"] = options.Input, ["voice"] = selection.Voice,
            ["response_format"] = format, ["stream"] = true, ["speed"] = options.Speed
        };
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Paxa Labs speech failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, read) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<PaxaSpeechResult> SendSpeechAsync(Dictionary<string, object?> payload, string format, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Paxa Labs speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        return new PaxaSpeechResult(audio, response.Content.Headers.ContentType?.MediaType ?? SpeechMimeType(format), response.GetHeaders());
    }

    private static (string Model, string Voice) ResolveSpeechSelection(string? model, string? explicitVoice)
    {
        var normalized = NormalizePaxaModel(model);
        var slash = normalized.IndexOf('/');
        var baseModel = slash < 0 ? normalized : normalized[..slash];
        var shortcutVoice = slash < 0 ? null : normalized[(slash + 1)..];
        if (!string.Equals(baseModel, TtsModelId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Unsupported Paxa Labs speech model '{model}'.");
        var voice = string.IsNullOrWhiteSpace(explicitVoice) ? shortcutVoice : explicitVoice;
        if (string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("A Paxa Labs voice is required, either in the model shortcut or the voice field.");
        return (baseModel, voice);
    }

    private static string ResolveSpeechFormat(string? format)
    {
        var value = string.IsNullOrWhiteSpace(format) ? "mp3" : format.Trim().ToLowerInvariant();
        if (value is not ("mp3" or "opus" or "wav")) throw new ArgumentException("Paxa Labs supports only mp3, opus, and wav speech formats.");
        return value;
    }

    private static string SpeechMimeType(string format) => format switch { "wav" => "audio/wav", "opus" => "audio/ogg", _ => "audio/mpeg" };
    private sealed record PaxaSpeechResult(byte[] Audio, string MimeType, Dictionary<string, string> Headers);
}
