using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMAPI;

public partial class LLMAPIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));

        var (audio, mediaType) = DecodeLLMAPIAudio(request.Audio, request.MediaType);
        var result = await TranscribeLLMAPIAsync(
            request.Model,
            audio,
            mediaType,
            GetLLMAPIProviderOptions(request.ProviderOptions),
            cancellationToken);
        var root = result.Root;

        var segments = root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array
            ? segmentArray.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = ReadLLMAPIString(segment, "text") ?? string.Empty,
                StartSecond = ReadLLMAPIFloat(segment, "start") ?? 0,
                EndSecond = ReadLLMAPIFloat(segment, "end") ?? 0
            }).ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = ReadLLMAPIString(root, "text") ?? string.Empty,
            Language = ReadLLMAPIString(root, "language"),
            DurationInSeconds = ReadLLMAPIFloat(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var fields = options.AdditionalProperties is null
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : options.AdditionalProperties.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        AddLLMAPIFormValue(fields, "language", options.Language);
        AddLLMAPIFormValue(fields, "prompt", options.Prompt);
        AddLLMAPIFormValue(fields, "response_format", options.ResponseFormat);
        if (options.Temperature is not null)
            fields["temperature"] = JsonSerializer.SerializeToElement(options.Temperature.Value);
        if (options.TimestampGranularities is not null)
            fields["timestamp_granularities[]"] = JsonSerializer.SerializeToElement(options.TimestampGranularities);

        var result = await TranscribeLLMAPIAsync(
            options.Model,
            memory.ToArray(),
            options.File.ContentType ?? "audio/mpeg",
            fields,
            cancellationToken,
            options.File.FileName);

        var text = ReadLLMAPIString(result.Root, "text") ?? string.Empty;
        if (string.Equals(options.ResponseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.ResponseFormat, "diarized_json", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAITranscriptionVerboseResponse
            {
                Text = text,
                Language = ReadLLMAPIString(result.Root, "language") ?? string.Empty,
                Duration = ReadLLMAPIFloat(result.Root, "duration") ?? 0
            };
        }

        return new OpenAITranscriptionResponse { Text = text };
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

    private async Task<LLMAPITranscriptionResult> TranscribeLLMAPIAsync(
        string model,
        byte[] audio,
        string mediaType,
        Dictionary<string, JsonElement>? fields,
        CancellationToken cancellationToken,
        string? fileName = null)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? ResolveLLMAPIAudioFileName(mediaType) : fileName);
        form.Add(new StringContent(model), "model");

        foreach (var field in fields ?? [])
        {
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || field.Key.Equals("file", StringComparison.OrdinalIgnoreCase)
                || field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)) continue;

            if (field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in field.Value.EnumerateArray())
                    form.Add(new StringContent(JsonElementToLLMAPIFormString(item)), field.Key);
            }
            else if (field.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                form.Add(new StringContent(JsonElementToLLMAPIFormString(field.Value)), field.Key);
            }
        }

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLMAPI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new LLMAPITranscriptionResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private static (byte[] Audio, string MediaType) DecodeLLMAPIAudio(object audio, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var value = audio switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(audio, CultureInfo.InvariantCulture)
        };
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Audio is required.", nameof(audio));

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0) throw new ArgumentException("The audio data URL is invalid.", nameof(audio));
            var metadata = value[5..comma];
            var semicolon = metadata.IndexOf(';');
            if (semicolon >= 0) metadata = metadata[..semicolon];
            if (!string.IsNullOrWhiteSpace(metadata)) mediaType = metadata;
            value = value[(comma + 1)..];
        }

        try { return (Convert.FromBase64String(value), string.IsNullOrWhiteSpace(mediaType) ? "audio/mpeg" : mediaType); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64 or a base64 data URL.", nameof(audio), exception); }
    }

    private static void AddLLMAPIFormValue(Dictionary<string, JsonElement> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields[name] = JsonSerializer.SerializeToElement(value);
    }

    private static string JsonElementToLLMAPIFormString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string ResolveLLMAPIAudioFileName(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "audio.wav",
            "audio/ogg" => "audio.ogg",
            "audio/mp4" or "audio/m4a" => "audio.m4a",
            "audio/aac" => "audio.aac",
            "audio/webm" => "audio.webm",
            "audio/flac" => "audio.flac",
            "audio/opus" => "audio.opus",
            _ => "audio.mp3"
        };

    private static string? ReadLLMAPIString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? ReadLLMAPIFloat(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetSingle(out var number) ? number : null;

    private sealed record LLMAPITranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);
}
