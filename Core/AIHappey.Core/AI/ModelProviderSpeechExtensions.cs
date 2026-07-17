using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderSpeechExtensions
{

    public static async IAsyncEnumerable<IAudioSpeechStreamEvent> SpeechStreamingAsync(
       this IModelProvider modelProvider,
       AudioSpeechRequest options,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = options.ToSpeechRequest();

        var result = await modelProvider.SpeechRequest(
            request,
            cancellationToken);

        foreach (var streamEvent in result.ToOpenAISpeechStreamEvents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public static async Task<(byte[] Audio, string MimeType)>
        OpenAICompatibleSpeechRequestAsync(
            this HttpClient httpClient,
            AudioSpeechRequest options,
            string? endpoint = "v1/audio/speech",
            CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateSpeechContent(options, streamFormat: "audio")
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Speech request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var mimeType =
            response.Content.Headers.ContentType?.MediaType
            ?? ResolveMimeType(options.ResponseFormat);

        return (audio, mimeType);
    }

    public static async IAsyncEnumerable<IAudioSpeechStreamEvent>
        OpenAICompatibleStreamingSpeechAsync(
            this HttpClient httpClient,
            AudioSpeechRequest options,
            string? endpoint = "v1/audio/speech",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateSpeechContent(options, streamFormat: "sse")
        };

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Streaming speech request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Streaming speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();

            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
                continue;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                continue;

            var type = typeElement.GetString();

            switch (type)
            {
                case "speech.audio.delta":
                    {
                        var speechEvent =
                            JsonSerializer.Deserialize<AudioSpeechStreamDelta>(data);

                        if (speechEvent != null)
                            yield return speechEvent;

                        break;
                    }

                case "speech.audio.done":
                    {
                        var speechEvent =
                            JsonSerializer.Deserialize<AudioSpeechStreamDone>(data);

                        if (speechEvent != null)
                            yield return speechEvent;

                        break;
                    }

                case "error":
                    {
                        throw new InvalidOperationException(
                            $"Streaming speech provider returned an error: {data}");
                    }
            }
        }
    }

    private static StringContent CreateSpeechContent(
        AudioSpeechRequest options,
        string streamFormat)
    {
        var json = JsonSerializer.SerializeToNode(options)?.AsObject()
                   ?? throw new InvalidOperationException(
                       "Could not serialize speech request.");

        json["stream_format"] = streamFormat;

        return new StringContent(
            json.ToJsonString(),
            Encoding.UTF8,
            "application/json");
    }

    private static string ResolveMimeType(string? responseFormat)
    {
        return responseFormat?.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }
}