using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WellSaid;

public sealed partial class WellSaidProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (payload, speakerId) = BuildPayload(request);
        var timestamp = DateTime.UtcNow;
        using var message = CreateSpeechMessage(payload);
        using var response = await _client.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        ValidateAudioResponse(response);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("WellSaid returned no speech audio.");
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = "audio/mpeg", Format = "mp3" },
            Request = new() { Body = payload },
            Response = new() { Timestamp = timestamp, ModelId = $"{GetIdentifier()}/{speakerId.ToString(CultureInfo.InvariantCulture)}" }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToSpeechRequest(options), cancellationToken)).ToOpenAISpeechAudio();

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.StreamFormat, "sse", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("WellSaid speech events require stream_format 'sse'.");
        var (payload, _) = BuildPayload(ToSpeechRequest(options));
        using var message = CreateSpeechMessage(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        ValidateAudioResponse(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var receivedAudio = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, count) };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("WellSaid returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private HttpRequestMessage CreateSpeechMessage(JsonElement payload)
    {
        var key = ResolveKey();
        var message = new HttpRequestMessage(HttpMethod.Post, "stream") { Content = JsonContent.Create(payload) };
        message.Headers.Add("X-Api-Key", key);
        message.Headers.Accept.ParseAdd("audio/mpeg");
        return message;
    }

    private static void ValidateAudioResponse(HttpResponseMessage response)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected a WellSaid audio/mpeg response.");
    }

    private (JsonElement Payload, int SpeakerId) BuildPayload(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Speed is not null || request.Instructions is not null || request.Language is not null)
            throw new NotSupportedException("WellSaid does not document standard speed, instructions, or language fields. Use native provider options.");
        if (request.OutputFormat is not null and not "mp3")
            throw new NotSupportedException("WellSaid streaming synthesis supports MP3 audio.");
        var model = request.Model.StartsWith("wellsaid/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["wellsaid/".Length..] : request.Model;
        if (!int.TryParse(model, NumberStyles.None, CultureInfo.InvariantCulture, out var modelSpeakerId))
            throw new ArgumentException("WellSaid models must contain a native integer avatar id.", nameof(request));
        var speakerId = modelSpeakerId;
        if (request.Voice is not null &&
            !int.TryParse(request.Voice, NumberStyles.None, CultureInfo.InvariantCulture, out speakerId))
            throw new ArgumentException("WellSaid voice must be a native integer avatar id.", nameof(request));

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("WellSaid provider options must be an object.", nameof(request));
            foreach (var property in native.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }
        payload["text"] = JsonSerializer.SerializeToElement(request.Text);
        payload["speaker_id"] = JsonSerializer.SerializeToElement(speakerId);
        return (JsonSerializer.SerializeToElement(payload), speakerId);
    }

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
        {
            var native = options.AdditionalProperties
                .Where(property => property.Key != "providerOptions")
                .ToDictionary(property => property.Key, property => property.Value.Clone(), StringComparer.Ordinal);
            if (options.AdditionalProperties.TryGetValue("providerOptions", out var envelope))
            {
                if (envelope.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("providerOptions must be an object.", nameof(options));
                if (envelope.TryGetProperty("wellsaid", out var providerOptions))
                {
                    if (providerOptions.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("WellSaid provider options must be an object.", nameof(options));
                    foreach (var property in providerOptions.EnumerateObject())
                        native[property.Name] = property.Value.Clone();
                }
            }
            request.ProviderOptions = new() { ["wellsaid"] = JsonSerializer.SerializeToElement(native) };
        }
        return request;
    }
}
