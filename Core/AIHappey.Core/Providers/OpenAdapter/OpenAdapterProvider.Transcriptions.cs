using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAdapter;

public partial class OpenAdapterProvider
{
    private static readonly JsonSerializerOptions OpenAdapterTranscriptionJson = new(JsonSerializerDefaults.Web);

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = DecodeAudio(request.Audio);
        var options = ReadProviderOptions(request.ProviderOptions);
        options["response_format"] = "verbose_json";
        var result = await SendTranscriptionRequestAsync(
            request.Model,
            audio,
            request.MediaType,
            ResolveAudioFileName(request.MediaType),
            options,
            cancellationToken);

        return ToNativeTranscriptionResponse(result, request.Model, options);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var fields = new Dictionary<string, object?>
        {
            ["language"] = options.Language,
            ["prompt"] = options.Prompt,
            ["response_format"] = responseFormat,
            ["temperature"] = options.Temperature,
            ["timestamp_granularities[]"] = options.TimestampGranularities
        };
        var result = await SendTranscriptionRequestAsync(
            options.Model,
            memory.ToArray(),
            string.IsNullOrWhiteSpace(options.File.ContentType) ? "application/octet-stream" : options.File.ContentType,
            string.IsNullOrWhiteSpace(options.File.FileName) ? ResolveAudioFileName(options.File.ContentType) : options.File.FileName,
            fields,
            cancellationToken);

        return ToOpenAITranscriptionResponse(result.Root, responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<OpenAdapterTranscriptionResult> SendTranscriptionRequestAsync(
        string model,
        byte[] audio,
        string mediaType,
        string fileName,
        Dictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");

        foreach (var field in fields)
        {
            if (field.Value is null || field.Key is "file" or "model" or "stream")
                continue;
            if (field.Value is IEnumerable<string> strings)
            {
                foreach (var value in strings)
                    form.Add(new StringContent(value), field.Key);
                continue;
            }

            var valueText = field.Value is JsonElement element
                ? ElementToFormValue(element)
                : Convert.ToString(field.Value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(valueText))
                form.Add(new StringContent(valueText), field.Key);
        }

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAdapter transcription failed ({(int)response.StatusCode}): {raw}");

        var contentType = response.Content.Headers.ContentType?.MediaType;
        JsonElement root;
        if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "text/json", StringComparison.OrdinalIgnoreCase) ||
            raw.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
        }
        else
        {
            root = JsonSerializer.SerializeToElement(new { text = raw }, OpenAdapterTranscriptionJson);
        }

        return new OpenAdapterTranscriptionResult(root, response.GetHeaders());
    }

    private TranscriptionResponse ToNativeTranscriptionResponse(
        OpenAdapterTranscriptionResult result,
        string requestedModel,
        Dictionary<string, object?> requestFields)
    {
        var root = result.Root;
        var segments = root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array
            ? segmentArray.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetString(segment, "text") ?? string.Empty,
                StartSecond = GetSingle(segment, "start") ?? 0,
                EndSecond = GetSingle(segment, "end") ?? 0
            }).ToArray()
            : [];

        return new TranscriptionResponse
        {
            Text = GetString(root, "text") ?? string.Empty,
            Language = GetString(root, "language"),
            DurationInSeconds = GetSingle(root, "duration"),
            Segments = segments,
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { response = root }, OpenAdapterTranscriptionJson)
            },
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(requestFields, OpenAdapterTranscriptionJson)
            },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = (GetString(root, "model") ?? requestedModel).ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static IOpenAITranscriptionResponse ToOpenAITranscriptionResponse(JsonElement root, string responseFormat)
    {
        var text = GetString(root, "text") ?? string.Empty;
        if (string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAITranscriptionVerboseResponse
            {
                Text = text,
                Language = GetString(root, "language") ?? string.Empty,
                Duration = GetDouble(root, "duration") ?? 0,
                Segments = ParseOpenAISegments(root),
                Words = ParseOpenAIWords(root)
            };
        }

        return new OpenAITranscriptionResponse { Text = text };
    }

    private static OpenAITranscriptionSegment[] ParseOpenAISegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select((segment, index) => new OpenAITranscriptionSegment
        {
            Id = GetInt32(segment, "id") ?? index,
            Seek = GetInt32(segment, "seek") ?? 0,
            Start = GetDouble(segment, "start") ?? 0,
            End = GetDouble(segment, "end") ?? 0,
            Text = GetString(segment, "text") ?? string.Empty,
            Tokens = GetInt32Array(segment, "tokens"),
            Temperature = GetSingle(segment, "temperature") ?? 0,
            AverageLogprob = GetSingle(segment, "avg_logprob") ?? 0,
            CompressionRatio = GetSingle(segment, "compression_ratio") ?? 0,
            NoSpeechProbability = GetSingle(segment, "no_speech_prob") ?? 0
        }).ToArray();
    }

    private static OpenAITranscriptionWord[] ParseOpenAIWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Select(word => new OpenAITranscriptionWord
        {
            Word = GetString(word, "word") ?? string.Empty,
            Start = GetDouble(word, "start") ?? 0,
            End = GetDouble(word, "end") ?? 0
        }).ToArray();
    }

    private Dictionary<string, object?> ReadProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>();
        if (providerOptions is null ||
            !providerOptions.TryGetValue(GetIdentifier(), out var options) ||
            options.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static byte[] DecodeAudio(object audio)
    {
        var value = audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(audio));
            value = value[(comma + 1)..];
        }
        return Convert.FromBase64String(value);
    }

    private static string ResolveAudioFileName(string? mediaType)
        => (mediaType ?? string.Empty).ToLowerInvariant() switch
        {
            "audio/ogg" or "audio/opus" => "audio.ogg",
            "audio/wav" or "audio/x-wav" => "audio.wav",
            "audio/webm" => "audio.webm",
            "audio/mp4" or "video/mp4" => "audio.mp4",
            "audio/flac" => "audio.flac",
            _ => "audio.mp3"
        };

    private static string ElementToFormValue(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? GetSingle(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private static int? GetInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static int[] GetInt32Array(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToArray()
            : [];

    private sealed record OpenAdapterTranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);
}
