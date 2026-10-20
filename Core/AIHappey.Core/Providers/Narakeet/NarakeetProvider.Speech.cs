using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Narakeet;

public sealed partial class NarakeetProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (message, format, voice) = CreateSpeechRequest(request);
        using var messageScope = message;
        var timestamp = DateTime.UtcNow;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("Narakeet returned no speech audio.");
        var headers = response.GetHeaders();
        var metadata = JsonSerializer.SerializeToElement(new { bytes = audio.Length, headers });
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = MimeType(format), Format = format },
            Request = new() { Body = request.Text },
            ProviderMetadata = new() { [GetIdentifier()] = metadata },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = $"{GetIdentifier()}/{voice}",
                Headers = headers,
                Body = metadata
            }
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
            throw new NotSupportedException("Narakeet speech events require stream_format 'sse'.");
        var (message, _, _) = CreateSpeechRequest(ToSpeechRequest(options));
        using var messageScope = message;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
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
            throw new InvalidOperationException("Narakeet returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private (HttpRequestMessage Message, string Format, string Voice) CreateSpeechRequest(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Instructions is not null || request.Language is not null)
            throw new NotSupportedException("Narakeet selects language by voice; instructions and language fields are unsupported.");
        var voice = request.Voice ?? (request.Model.StartsWith("narakeet/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["narakeet/".Length..] : request.Model);
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("A Narakeet voice name is required.", nameof(request));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Narakeet provider options must be a query parameter object.", nameof(request));
            foreach (var property in native.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Narakeet query parameter values must be scalar.", nameof(request));
                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()! : property.Value.GetRawText();
            }
        }
        parameters["voice"] = voice;
        if (request.Speed is not null)
            parameters["voice-speed"] = request.Speed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var format = request.OutputFormat ?? "mp3";
        if (format is not ("mp3" or "m4a"))
            throw new NotSupportedException("Narakeet short-content speech supports mp3 and m4a.");
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var message = CreateRequest(HttpMethod.Post, $"text-to-speech/{format}?{query}");
        message.Headers.Accept.ParseAdd("application/octet-stream");
        message.Content = new StringContent(request.Text, Encoding.UTF8, "text/plain");
        return (message, format, voice);
    }

    private static string MimeType(string format) => format == "mp3" ? "audio/mpeg" : "audio/mp4";

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
            request.ProviderOptions = new() { ["narakeet"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        return request;
    }
}
