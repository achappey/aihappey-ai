using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        var bytes = DecodeEdenAIAudio(audio);
        var providerOptions = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            ? options
            : default;
        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "file", "audio" + GetEdenAIAudioExtension(request.MediaType));
        form.Add(new StringContent(request.Model), "model");
        AddEdenAITranscriptionOptions(form, providerOptions);

        ApplyAuthHeader();
        using var response = await _client.PostAsync("v3/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EdenAI transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ReadEdenAIString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("EdenAI transcription response did not contain text.");

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadEdenAIString(root, "language"),
            DurationInSeconds = ReadEdenAIFloat(root, "duration"),
            Segments = ReadEdenAITranscriptionSegments(root),
            ProviderMetadata = BuildEdenAITranscriptionMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }




    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static byte[] DecodeEdenAIAudio(string audio)
    {
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(audio));
            audio = audio[(comma + 1)..];
        }

        return Convert.FromBase64String(audio);
    }

    private static void AddEdenAITranscriptionOptions(MultipartFormDataContent form, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.Name is not ("language" or "prompt" or "response_format" or "temperature" or "user" or "timestamp_granularities"))
                continue;

            form.Add(new StringContent(property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText()), property.Name);
        }
    }

    private static string GetEdenAIAudioExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "audio/ogg" => ".ogg", "audio/flac" => ".flac", "audio/aac" => ".aac", _ => ".bin"
        };

    private Dictionary<string, JsonElement> BuildEdenAITranscriptionMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = root
        };

        if (root.TryGetProperty("cost", out var cost) && TryGetDecimal(cost, out var parsedCost))
            metadata["gateway"] = JsonSerializer.SerializeToElement(new { cost = parsedCost });

        return metadata;
    }

    private static float? ReadEdenAIFloat(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.TryGetSingle(out var result) ? result : null;

    private static IEnumerable<TranscriptionSegment> ReadEdenAITranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return [];

        return segments.EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Object)
            .Select(segment => new TranscriptionSegment
            {
                Text = ReadEdenAIString(segment, "text") ?? string.Empty,
                StartSecond = ReadEdenAIFloat(segment, "start") ?? 0,
                EndSecond = ReadEdenAIFloat(segment, "end") ?? 0
            })
            .ToList();
    }

}
