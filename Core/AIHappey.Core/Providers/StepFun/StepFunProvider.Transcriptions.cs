using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = ReadStepFunTranscriptionAudio(request.Audio);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildStepFunTranscriptionPayload(request.Model, audio, request.MediaType, metadata);
        var requestBody = JsonSerializer.Serialize(payload, StepFunSpeechJson);
        var text = new StringBuilder();
        JsonElement? doneEvent = null;
        Dictionary<string, string>? responseHeaders = null;

        await foreach (var streamEvent in SendStepFunTranscriptionAsync(requestBody, cancellationToken))
        {
            responseHeaders ??= streamEvent.Headers;
            if (streamEvent.Type == "transcript.text.delta")
                text.Append(streamEvent.Text);
            else if (streamEvent.Type == "transcript.text.done")
            {
                if (!string.IsNullOrWhiteSpace(streamEvent.Text))
                {
                    text.Clear();
                    text.Append(streamEvent.Text);
                }

                doneEvent = streamEvent.Raw;
            }
        }

        return new TranscriptionResponse
        {
            Text = text.ToString(),
            Language = TryReadStepFunTranscriptionLanguage(payload),
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(doneEvent),
            Request = new TranscriptionRequestItem { Body = requestBody },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = responseHeaders,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = doneEvent
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var audio = ReadStepFunTranscriptionAudio(request.Audio);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildStepFunTranscriptionPayload(request.Model, audio, request.MediaType, metadata);
        var requestBody = JsonSerializer.Serialize(payload, StepFunSpeechJson);
        var transcript = new StringBuilder();
        var emittedDone = false;

        await foreach (var streamEvent in SendStepFunTranscriptionAsync(requestBody, cancellationToken))
        {
            if (streamEvent.Type == "transcript.text.delta")
            {
                transcript.Append(streamEvent.Text);
                yield return new OpenAITranscriptionTextDelta { Delta = streamEvent.Text };
            }
            else if (streamEvent.Type == "transcript.text.done")
            {
                var text = string.IsNullOrWhiteSpace(streamEvent.Text)
                    ? transcript.ToString()
                    : streamEvent.Text;
                emittedDone = true;
                yield return new OpenAITranscriptionTextDone { Text = text };
            }
        }

        if (!emittedDone)
            yield return new OpenAITranscriptionTextDone { Text = transcript.ToString() };
    }

    private async IAsyncEnumerable<StepFunTranscriptionEvent> SendStepFunTranscriptionAsync(
        string requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/asr/sse")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"StepFun transcription failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        var headers = response.GetHeaders();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                var parsed = ParseStepFunTranscriptionEvent(dataLines, headers);
                dataLines.Clear();
                if (parsed is not null)
                    yield return parsed;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var finalEvent = ParseStepFunTranscriptionEvent(dataLines, headers);
        if (finalEvent is not null)
            yield return finalEvent;
    }

    private static StepFunTranscriptionEvent? ParseStepFunTranscriptionEvent(
        IReadOnlyCollection<string> dataLines,
        Dictionary<string, string> headers)
    {
        if (dataLines.Count == 0)
            return null;

        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            return null;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = ReadStepFunTranscriptionString(root, "type");

        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            var message = ReadStepFunTranscriptionString(root, "message") ?? data;
            throw new InvalidOperationException($"StepFun transcription stream returned an error: {message}");
        }

        if (type is not "transcript.text.delta" and not "transcript.text.done")
            return null;

        var text = type == "transcript.text.delta"
            ? ReadStepFunTranscriptionString(root, "delta") ?? string.Empty
            : ReadStepFunTranscriptionString(root, "text") ?? string.Empty;

        return new StepFunTranscriptionEvent(type, text, root.Clone(), headers);
    }

    private static Dictionary<string, object?> BuildStepFunTranscriptionPayload(
        string model,
        string audio,
        string mediaType,
        JsonElement metadata)
    {
        var root = CopyStepFunJsonObject(metadata);
        var audioOptions = ReadStepFunNestedObject(metadata, "audio");
        var inputOptions = ReadStepFunNestedObject(metadata, "audio", "input");
        var transcriptionOptions = ReadStepFunNestedObject(metadata, "audio", "input", "transcription");
        var formatOptions = ReadStepFunNestedObject(metadata, "audio", "input", "format");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("language", out var language))
                transcriptionOptions["language"] = language.Clone();
            if (metadata.TryGetProperty("enable_itn", out var enableItn))
                transcriptionOptions["enable_itn"] = enableItn.Clone();
        }

        transcriptionOptions["model"] = model;
        if (!formatOptions.ContainsKey("type"))
            formatOptions["type"] = ResolveStepFunTranscriptionFormat(mediaType);

        inputOptions["transcription"] = transcriptionOptions;
        inputOptions["format"] = formatOptions;
        audioOptions["input"] = inputOptions;
        audioOptions["data"] = audio;
        root["audio"] = audioOptions;

        return root;
    }

    private static Dictionary<string, object?> CopyStepFunJsonObject(JsonElement value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in value.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private static Dictionary<string, object?> ReadStepFunNestedObject(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
                return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return CopyStepFunJsonObject(current);
    }

    private static string ReadStepFunTranscriptionAudio(object? audio)
    {
        var value = audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        return value.RemoveDataUrlPrefix();
    }

    private static string ResolveStepFunTranscriptionFormat(string mediaType)
        => mediaType.Trim().ToLowerInvariant() switch
        {
            "audio/ogg" or "application/ogg" => "ogg",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
            "audio/pcm" or "audio/l16" or "application/octet-stream" => "pcm",
            _ => throw new NotSupportedException($"StepFun transcription does not support media type '{mediaType}'.")
        };

    private static string? TryReadStepFunTranscriptionLanguage(Dictionary<string, object?> payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, StepFunSpeechJson));
        var root = document.RootElement;
        return root.TryGetProperty("audio", out var audio)
               && audio.TryGetProperty("input", out var input)
               && input.TryGetProperty("transcription", out var transcription)
               && transcription.TryGetProperty("language", out var language)
               && language.ValueKind == JsonValueKind.String
            ? language.GetString()
            : null;
    }

    private static string? ReadStepFunTranscriptionString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record StepFunTranscriptionEvent(
        string Type,
        string Text,
        JsonElement Raw,
        Dictionary<string, string> Headers);
}
