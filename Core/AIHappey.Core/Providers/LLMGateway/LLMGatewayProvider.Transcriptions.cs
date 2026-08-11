using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private const string LLMGatewayTranscriptionsEndpoint = "v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var encodedAudio = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(encodedAudio))
            throw new ArgumentException("Audio is required.", nameof(request));

        encodedAudio = StripLLMGatewayAudioDataUrl(encodedAudio);

        byte[] audio;
        try
        {
            audio = Convert.FromBase64String(encodedAudio);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must contain valid base64 data.", nameof(request), exception);
        }

        if (audio.Length == 0)
            throw new ArgumentException("Audio is required.", nameof(request));

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        AddLLMGatewayTranscriptionProviderOptions(form, request.ProviderOptions);

        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        using var response = await _client.PostAsync(LLMGatewayTranscriptionsEndpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"LLM Gateway transcription failed ({(int)response.StatusCode})."
                : $"LLM Gateway transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ReadLLMGatewayTranscriptionString(root, "text")
            ?? throw new InvalidOperationException("LLM Gateway transcription response did not include text.");

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadLLMGatewayTranscriptionString(root, "language"),
            DurationInSeconds = ReadLLMGatewayTranscriptionFloat(root, "duration", "duration_in_seconds"),
            Segments = ReadLLMGatewayTranscriptionSegments(root),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(new
                {
                    request.Model,
                    request.MediaType,
                    audioBytes = audio.Length,
                    request.ProviderOptions
                }, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                ModelId = (ReadLLMGatewayTranscriptionString(root, "model") ?? request.Model).ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            endpoint: LLMGatewayTranscriptionsEndpoint,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // LLM Gateway does not document native transcription SSE. Preserve the
        // public streaming contract by adapting its non-streaming response.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string StripLLMGatewayAudioDataUrl(string audio)
    {
        audio = audio.Trim();
        if (!audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return audio;

        var comma = audio.IndexOf(',');
        if (comma < 0)
            throw new ArgumentException("Audio data URL is invalid.", nameof(audio));

        return audio[(comma + 1)..];
    }

    private static void AddLLMGatewayTranscriptionProviderOptions(
        MultipartFormDataContent form,
        IReadOnlyDictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue("llmgateway", out var options)
            || options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("file") || property.NameEquals("model") || property.NameEquals("stream"))
                continue;

            AddLLMGatewayTranscriptionMultipartValue(form, property.Name, property.Value);
        }
    }

    private static void AddLLMGatewayTranscriptionMultipartValue(
        MultipartFormDataContent form,
        string name,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    AddLLMGatewayTranscriptionMultipartValue(form, $"{name}[{property.Name}]", property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddLLMGatewayTranscriptionMultipartValue(form, $"{name}[]", item);
                break;
            case JsonValueKind.String:
                form.Add(new StringContent(value.GetString() ?? string.Empty, Encoding.UTF8), name);
                break;
            case JsonValueKind.Number:
                form.Add(new StringContent(value.GetRawText(), Encoding.UTF8), name);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                form.Add(new StringContent(value.GetBoolean() ? "true" : "false", Encoding.UTF8), name);
                break;
        }
    }

    private static string? ReadLLMGatewayTranscriptionString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? ReadLLMGatewayTranscriptionFloat(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetSingle(out var number))
                return number;
        }

        return null;
    }

    private static IReadOnlyList<TranscriptionSegment> ReadLLMGatewayTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return [];

        return segments.EnumerateArray().Select(segment => new TranscriptionSegment
        {
            Text = ReadLLMGatewayTranscriptionString(segment, "text") ?? string.Empty,
            StartSecond = ReadLLMGatewayTranscriptionFloat(segment, "start", "start_second") ?? 0,
            EndSecond = ReadLLMGatewayTranscriptionFloat(segment, "end", "end_second") ?? 0
        }).ToList();
    }
}
