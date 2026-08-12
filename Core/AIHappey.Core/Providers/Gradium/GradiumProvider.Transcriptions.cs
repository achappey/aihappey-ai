using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Gradium;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider
{
    private const string TranscriptionModel = "default";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var model = NormalizeTranscriptionModel(request.Model);
        if (!string.Equals(model, TranscriptionModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} transcription model '{model}' is not supported.");

        var (contentType, inputFormat) = NormalizeTranscriptionMediaType(request.MediaType);
        var audio = DecodeTranscriptionAudio(request.Audio);
        var metadata = request.GetProviderMetadata<GradiumTranscriptionProviderMetadata>(GetIdentifier());
        ValidateTranscriptionTemperature(metadata?.Temperature);

        var jsonConfig = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            jsonConfig["language"] = metadata.Language.Trim();
        if (metadata?.Temperature is not null)
            jsonConfig["temp"] = metadata.Temperature.Value;

        var requestUri = BuildTranscriptionRequestUri(model, inputFormat, jsonConfig);
        var requestBody = JsonSerializer.Serialize(new
        {
            model,
            input_format = inputFormat,
            json_config = jsonConfig.Count == 0 ? null : jsonConfig,
            audio_bytes = audio.Length
        }, SpeechJsonOptions);

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new ByteArrayContent(audio)
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("application/x-ndjson");

        var now = DateTime.UtcNow;
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} transcription failed ({(int)response.StatusCode}): {error}");
        }

        var text = new StringBuilder();
        var segments = new List<GradiumTranscriptionSegment>();
        var events = new List<JsonElement>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"Failed to parse {ProviderName} transcription event: {line}", exception);
            }

            using (document)
            {
                var root = document.RootElement;
                events.Add(root.Clone());
                var type = ReadGradiumString(root, "type");

                switch (type)
                {
                    case "text":
                        var value = ReadGradiumString(root, "text") ?? string.Empty;
                        text.Append(value);
                        segments.Add(new GradiumTranscriptionSegment
                        {
                            Text = value,
                            Start = ReadGradiumSingle(root, "start_s") ?? 0
                        });
                        break;

                    case "end_text":
                        var stop = ReadGradiumSingle(root, "stop_s") ?? 0;
                        var pending = segments.LastOrDefault(segment => segment.End is null);
                        if (pending is not null)
                            pending.End = stop;
                        break;

                    case "error":
                        throw new InvalidOperationException(
                            $"{ProviderName} transcription failed: {ReadGradiumString(root, "message") ?? root.GetRawText()}");

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported {ProviderName} transcription event type '{type ?? "<missing>"}'.");
                }
            }
        }

        var duration = segments.Count == 0
            ? (float?)null
            : segments.Max(segment => segment.End ?? segment.Start);

        return new TranscriptionResponse
        {
            Text = text.ToString(),
            Language = metadata?.Language,
            DurationInSeconds = duration,
            Segments = segments.Select(segment => new TranscriptionSegment
            {
                Text = segment.Text,
                StartSecond = segment.Start,
                EndSecond = segment.End ?? segment.Start
            }),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model,
                    input_format = inputFormat,
                    json_config = jsonConfig,
                    events
                }, SpeechJsonOptions)
            },
            Request = new TranscriptionRequestItem { Body = requestBody },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = JsonSerializer.SerializeToElement(events, SpeechJsonOptions)
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        ValidateTranscriptionTemperature(options.Temperature);
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string BuildTranscriptionRequestUri(
        string model,
        string inputFormat,
        Dictionary<string, object?> jsonConfig)
    {
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(model)}",
            $"input_format={Uri.EscapeDataString(inputFormat)}"
        };

        if (jsonConfig.Count > 0)
        {
            query.Add("json_config=" + Uri.EscapeDataString(
                JsonSerializer.Serialize(jsonConfig, SpeechJsonOptions)));
        }

        return "api/post/speech/asr?" + string.Join('&', query);
    }

    private static string NormalizeTranscriptionModel(string model)
    {
        var normalized = model.Trim();
        var prefix = ProviderId + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static (string ContentType, string InputFormat) NormalizeTranscriptionMediaType(string mediaType)
        => mediaType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => ("audio/wav", "wav"),
            "audio/pcm" or "audio/l16" => ("audio/pcm", "pcm"),
            "audio/ogg" => ("audio/ogg", "opus"),
            "audio/opus" => ("audio/opus", "opus"),
            var value => throw new NotSupportedException(
                $"{ProviderName} transcription does not support media type '{value}'. Supported types are WAV, PCM, Ogg, and Opus.")
        };

    private static byte[] DecodeTranscriptionAudio(object? audio)
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
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("Audio data URL must contain base64 data.", nameof(audio));
            value = value[(marker + ";base64,".Length)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length > 0
                ? bytes
                : throw new ArgumentException("Audio is required.", nameof(audio));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must contain valid base64 data.", nameof(audio), exception);
        }
    }

    private static void ValidateTranscriptionTemperature(float? temperature)
    {
        if (temperature is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(temperature), "Gradium transcription temperature must be between 0 and 1.");
    }

    private static string? ReadGradiumString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? ReadGradiumSingle(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number)
            ? number
            : null;

    private sealed class GradiumTranscriptionSegment
    {
        public string Text { get; init; } = string.Empty;
        public float Start { get; init; }
        public float? End { get; set; }
    }
}
