using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NEARAI;

public partial class NEARAIProvider
{


    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        var (audio, mediaType) = NEARAIDecodeBase64(request.Audio, request.MediaType);
        var result = await NEARAITranscribeAsync(request.Model, audio, mediaType, request.ProviderOptions, null, cancellationToken);
        return NEARAIToTranscriptionResponse(result, request.Model);
    }


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        return NEARAITranscribeOpenAIAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        return NEARAITranscriptionStream(options, cancellationToken);
    }

    private async Task<IOpenAITranscriptionResponse> NEARAITranscribeOpenAIAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken)
    {
        await using var source = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        var result = await NEARAITranscribeAsync(options.Model, memory.ToArray(), options.File.ContentType ?? "audio/mpeg", null,
            new Dictionary<string, object?>
            {
                ["language"] = options.Language,
                ["prompt"] = options.Prompt,
                ["response_format"] = options.ResponseFormat,
                ["temperature"] = options.Temperature,
                ["timestamp_granularities[]"] = options.TimestampGranularities
            }, cancellationToken);
        var text = NEARAIString(result.Root, "text") ?? string.Empty;
        return string.Equals(options.ResponseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase)
            ? new OpenAITranscriptionVerboseResponse { Text = text, Language = NEARAIString(result.Root, "language") ?? string.Empty, Duration = NEARAIDouble(result.Root, "duration") ?? 0 }
            : new OpenAITranscriptionResponse { Text = text };
    }

    private async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> NEARAITranscriptionStream(OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<NEARAITranscriptionResult> NEARAITranscribeAsync(string model, byte[] audio, string mediaType, Dictionary<string, JsonElement>? providerOptions, Dictionary<string, object?>? knownOptions, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", NEARAIAudioFileName(mediaType));
        form.Add(new StringContent(model), "model");
        foreach (var property in NEARAITranscriptionFields(providerOptions, knownOptions))
            if (property.Value is not null)
                if (property.Value is IEnumerable<string> values)
                    foreach (var value in values) form.Add(new StringContent(value), property.Key);
                else form.Add(new StringContent(property.Value is JsonElement element ? element.GetRawText() : Convert.ToString(property.Value, System.Globalization.CultureInfo.InvariantCulture)!), property.Key);
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NEARAI transcription request failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return new NEARAITranscriptionResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private Dictionary<string, object?> NEARAITranscriptionFields(Dictionary<string, JsonElement>? providerOptions, Dictionary<string, object?>? knownOptions)
    {
        var result = NEARAIJsonObject(providerOptions, "file", "model");
        foreach (var field in knownOptions ?? []) if (field.Value is not null) result[field.Key] = field.Value;
        return result;
    }

    private TranscriptionResponse NEARAIToTranscriptionResponse(NEARAITranscriptionResult result, string requestedModel)
    {
        var root = result.Root;
        var segments = root.TryGetProperty("segments", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(segment => new TranscriptionSegment { Text = NEARAIString(segment, "text") ?? string.Empty, StartSecond = (float)(NEARAIDouble(segment, "start") ?? 0), EndSecond = (float)(NEARAIDouble(segment, "end") ?? 0) }).ToList()
            : [];
        return new TranscriptionResponse
        {
            Text = NEARAIString(root, "text") ?? string.Empty,
            Language = NEARAIString(root, "language"),
            DurationInSeconds = (float?)NEARAIDouble(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = (NEARAIString(root, "model") ?? requestedModel).ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static (byte[] Audio, string MediaType) NEARAIDecodeBase64(object audio, string mediaType)
    {
        var value = audio is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : audio?.ToString();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Audio is required.", nameof(audio));
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma <= 5 || comma == value.Length - 1) throw new FormatException("Invalid audio data URL.");
            var header = value[5..comma];
            mediaType = header.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            value = value[(comma + 1)..];
        }
        return (Convert.FromBase64String(value), mediaType);
    }

    private static string NEARAIAudioFileName(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => "audio.wav",
        "audio/webm" => "audio.webm",
        "audio/flac" => "audio.flac",
        "audio/ogg" => "audio.ogg",
        "audio/mp4" or "audio/m4a" => "audio.m4a",
        _ => "audio.mp3"
    };

    private static string? NEARAIString(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static double? NEARAIDouble(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) ? value : null;
    private sealed record NEARAITranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);


}
