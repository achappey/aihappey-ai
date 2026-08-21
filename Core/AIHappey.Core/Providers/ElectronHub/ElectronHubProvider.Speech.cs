using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ElectronHub;

public partial class ElectronHubProvider
{
    private const string ElectronHubSpeechEndpoint = "v1/audio/speech";

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var payload = ElectronHubSpeechOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["response_format"] = request.OutputFormat;
        if (request.Speed.HasValue) payload["speed"] = request.Speed.Value;
        if (!string.IsNullOrWhiteSpace(request.Instructions)) payload["instructions"] = request.Instructions;

        ApplyAuthHeader();
        using var response = await _client.PostAsync(ElectronHubSpeechEndpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ElectronHub speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? ElectronHubSpeechMimeType(request.OutputFormat);
        var format = request.OutputFormat ?? ElectronHubSpeechFormat(mimeType);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(bytes), MimeType = mimeType, Format = format },
            Warnings = string.IsNullOrWhiteSpace(request.Language) ? [] : [new { type = "unsupported", feature = "language" }],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();
        return _client.OpenAICompatibleSpeechRequestAsync(options, ElectronHubSpeechEndpoint, cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static JsonObject ElectronHubSpeechOptions(IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (options is null || !options.TryGetValue("electronhub", out var value) || value.ValueKind != JsonValueKind.Object) return [];
        return JsonNode.Parse(value.GetRawText())?.AsObject() ?? [];
    }

    private static string ElectronHubSpeechMimeType(string? format) => format?.ToLowerInvariant() switch
    { "opus" => "audio/opus", "aac" => "audio/aac", "flac" => "audio/flac", "wav" => "audio/wav", "pcm" => "audio/pcm", _ => "audio/mpeg" };

    private static string ElectronHubSpeechFormat(string mimeType) => mimeType.ToLowerInvariant() switch
    { "audio/opus" => "opus", "audio/aac" => "aac", "audio/flac" => "flac", "audio/wav" => "wav", "audio/pcm" => "pcm", _ => "mp3" };
}
