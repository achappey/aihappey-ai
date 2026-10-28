using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Munito;

/// <summary>POST /audio/transcriptions (multipart). Munito documents json and text responses only.</summary>
public sealed partial class MunitoProvider
{
    private const long MaxAudioBytes = 25L * 1024 * 1024;
    private const string TranscriptionsPath = "audio/transcriptions";
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".ogg", ".webm", ".mp4", ".m4a"];

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateTranscriptionOptions(options);
        ApplyAuthHeader();

        return await _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            endpoint: TranscriptionsPath,
            cancellationToken: cancellationToken);
    }

    // Streaming transcription is not documented; the final result is replayed as stream events.
    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Munito does not document streaming transcription.");

    private static void ValidateTranscriptionOptions(OpenAITranscriptionRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.File is null || options.File.Length == 0)
            throw new ArgumentException("An audio file is required.", nameof(options));
        if (options.File.Length > MaxAudioBytes)
            throw new ArgumentException("Munito transcription files must be at most 25 MB.", nameof(options));
        var extension = Path.GetExtension(options.File.FileName);
        if (!string.IsNullOrEmpty(extension) && !AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Munito supports wav, mp3, flac, ogg, webm, mp4 and m4a audio.", nameof(options));
        if (options.Prompt is { Length: > 2000 })
            throw new ArgumentException("Munito transcription prompts must be at most 2000 characters.", nameof(options));
        if (options.Temperature is < 0 or > 1)
            throw new ArgumentException("Munito transcription temperature must be between 0 and 1.", nameof(options));
        if (options.ResponseFormat is not (null or "json" or "text"))
            throw new ArgumentException("Munito transcription supports only the json and text response formats.", nameof(options));
        if (options.Stream == true)
            throw new ArgumentException("Munito does not document streaming transcription.", nameof(options));
        if (options.TimestampGranularities is { Length: > 0 } || options.Include is { Length: > 0 }
            || options.ChunkingStrategy is not null || options.KnownSpeakerNames is { Length: > 0 }
            || options.KnownSpeakerReferences is { Length: > 0 } || options.AdditionalProperties?.Count > 0)
            throw new ArgumentException(
                "Munito transcription supports only file, model, language, prompt, response_format and temperature.",
                nameof(options));
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(audioString); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64.", nameof(request), exception); }
        if (bytes.Length > MaxAudioBytes)
            throw new ArgumentException("Munito transcription files must be at most 25 MB.", nameof(request));

        string? region = null;
        var fields = new List<(string Name, string Value)>();
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "region":
                        region = NormalizeRegion(property.Value.GetString());
                        break;
                    case "language" or "prompt" when property.Value.ValueKind == JsonValueKind.String:
                        fields.Add((property.Name, property.Value.GetString()!));
                        break;
                    case "temperature" when property.Value.ValueKind == JsonValueKind.Number:
                        fields.Add((property.Name, property.Value.GetRawText()));
                        break;
                    case "response_format" when property.Value.GetString() is "json" or "text":
                        fields.Add((property.Name, property.Value.GetString()!));
                        break;
                    default:
                        throw new ArgumentException($"Munito transcription does not support provider option '{property.Name}'.", nameof(request));
                }
            }
        }

        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(NormalizeModelId(request.Model), Encoding.UTF8), "model");
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value, Encoding.UTF8), name);

        using var response = await _client.PostAsync(ResolveUrl(region, TranscriptionsPath), form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Munito transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        string text;
        object body;
        try
        {
            using var document = JsonDocument.Parse(raw);
            text = document.RootElement.TryGetProperty("text", out var textElement)
                   && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // response_format=text returns plain text.
            text = raw;
            body = raw;
        }

        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model,
                Body = body
            }
        };
    }
}
