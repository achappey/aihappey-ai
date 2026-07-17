using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderTranscriptionCompatibilityExtensions
{
    private static readonly JsonSerializerOptions OpenAITranscriptionJsonOptions =
          new(JsonSerializerDefaults.Web)
          {
              PropertyNameCaseInsensitive = true,
              DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
          };

    public static async Task<IOpenAITranscriptionResponse>
        OpenAICompatibleTranscriptionRequestAsync(
            this HttpClient httpClient,
            OpenAITranscriptionRequest options,
            string? endpoint = "v1/audio/transcriptions",
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateTranscriptionContent(options, forceStream: false)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateTranscriptionRequestException(
                "Transcription",
                response,
                raw);
        }

        return DeserializeTranscriptionResponse(
            raw,
            options.ResponseFormat,
            response.Content.Headers.ContentType?.MediaType);
    }

    public static async IAsyncEnumerable<IOpenAITranscriptionStreamEvent>
        OpenAICompatibleTranscriptionStreamingAsync(
            this HttpClient httpClient,
            OpenAITranscriptionRequest options,
            string? endpoint = "v1/audio/transcriptions",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateTranscriptionContent(options, forceStream: true)
        };

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(
                cancellationToken);

            throw CreateTranscriptionRequestException(
                "Streaming transcription",
                response,
                error);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ParseStreamEvent(
                    eventName,
                    dataLines))
                {
                    yield return streamEvent;
                }

                eventName = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith(
                    "event:",
                    StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith(
                    "data:",
                    StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        // Some compatible providers do not terminate the final SSE event
        // with an empty line.
        foreach (var streamEvent in ParseStreamEvent(
            eventName,
            dataLines))
        {
            yield return streamEvent;
        }
    }

    private static MultipartFormDataContent CreateTranscriptionContent(
        OpenAITranscriptionRequest options,
        bool forceStream)
    {
        if (options.File == null)
        {
            throw new ArgumentException(
                "A transcription file is required.",
                nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException(
                "A transcription model is required.",
                nameof(options));
        }

        var content = new MultipartFormDataContent();

        AddFile(
            content,
            "file",
            options.File,
            "audio");

        AddMultipartString(
            content,
            "model",
            options.Model);

        AddMultipartString(
            content,
            "language",
            options.Language);

        AddMultipartString(
            content,
            "prompt",
            options.Prompt);

        AddMultipartString(
            content,
            "response_format",
            options.ResponseFormat);

        AddMultipartString(
            content,
            "temperature",
            options.Temperature?.ToString(
                CultureInfo.InvariantCulture));

        AddMultipartArray(
            content,
            "timestamp_granularities",
            options.TimestampGranularities);

        AddMultipartString(
            content,
            "stream",
            forceStream
                ? "true"
                : options.Stream?.ToString().ToLowerInvariant());

        AddMultipartArray(
            content,
            "include",
            options.Include);

        AddChunkingStrategy(
            content,
            options.ChunkingStrategy);

        AddMultipartArray(
            content,
            "known_speaker_names",
            options.KnownSpeakerNames);

        AddMultipartArray(
            content,
            "known_speaker_references",
            options.KnownSpeakerReferences);

        return content;
    }

    private static void AddFile(
        MultipartFormDataContent content,
        string name,
        Microsoft.AspNetCore.Http.IFormFile file,
        string fallbackFileName)
    {
        var fileContent = new StreamContent(file.OpenReadStream());

        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(file.ContentType)
                ? MediaTypeNames.Application.Octet
                : file.ContentType);

        var fileName = string.IsNullOrWhiteSpace(file.FileName)
            ? fallbackFileName
            : file.FileName;

        content.Add(fileContent, name, fileName);
    }

    private static void AddMultipartString(
        MultipartFormDataContent content,
        string name,
        string? value)
    {
        if (value == null)
            return;

        content.Add(
            new StringContent(value, Encoding.UTF8),
            name);
    }

    private static void AddMultipartArray(
        MultipartFormDataContent content,
        string name,
        IEnumerable<string>? values)
    {
        if (values == null)
            return;

        foreach (var value in values)
        {
            if (value == null)
                continue;

            content.Add(
                new StringContent(value, Encoding.UTF8),
                $"{name}[]");
        }
    }

    private static void AddChunkingStrategy(
        MultipartFormDataContent content,
        object? chunkingStrategy)
    {
        if (chunkingStrategy == null)
            return;

        if (chunkingStrategy is string stringValue)
        {
            AddMultipartString(
                content,
                "chunking_strategy",
                stringValue);

            return;
        }

        var element = chunkingStrategy is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(
                chunkingStrategy,
                OpenAITranscriptionJsonOptions);

        AddMultipartJsonValue(
            content,
            "chunking_strategy",
            element);
    }

    private static void AddMultipartJsonValue(
        MultipartFormDataContent content,
        string name,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddMultipartJsonValue(
                        content,
                        $"{name}[{property.Name}]",
                        property.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddMultipartJsonValue(
                        content,
                        $"{name}[]",
                        item);
                }

                break;

            case JsonValueKind.String:
                AddMultipartString(
                    content,
                    name,
                    element.GetString());

                break;

            case JsonValueKind.Number:
                AddMultipartString(
                    content,
                    name,
                    element.GetRawText());

                break;

            case JsonValueKind.True:
                AddMultipartString(
                    content,
                    name,
                    "true");

                break;

            case JsonValueKind.False:
                AddMultipartString(
                    content,
                    name,
                    "false");

                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported multipart JSON value for '{name}'.");
        }
    }

    private static IEnumerable<IOpenAITranscriptionStreamEvent>
        ParseStreamEvent(
            string? eventName,
            List<string> dataLines)
    {
        if (dataLines.Count == 0)
            yield break;

        var data = string.Join("\n", dataLines).Trim();

        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(
                data,
                "[DONE]",
                StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(data);

        var root = document.RootElement;
        var type = ReadStreamEventType(root, eventName);

        IOpenAITranscriptionStreamEvent? streamEvent = type switch
        {
            "transcript.text.delta" =>
                JsonSerializer.Deserialize<OpenAITranscriptionTextDelta>(
                    data,
                    OpenAITranscriptionJsonOptions),

            "transcript.text.done" =>
                JsonSerializer.Deserialize<OpenAITranscriptionTextDone>(
                    data,
                    OpenAITranscriptionJsonOptions),

            "transcript.text.segment" =>
                JsonSerializer.Deserialize<OpenAITranscriptionTextSegment>(
                    data,
                    OpenAITranscriptionJsonOptions),

            "error" => throw new InvalidOperationException(
                $"Transcription stream provider returned an error: {data}"),

            _ => null
        };

        if (streamEvent != null)
            yield return streamEvent;
    }

    private static string? ReadStreamEventType(
        JsonElement root,
        string? eventName)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String)
        {
            return typeElement.GetString();
        }

        return eventName;
    }

    private static IOpenAITranscriptionResponse
        DeserializeTranscriptionResponse(
            string raw,
            string? responseFormat,
            string? mediaType)
    {
        var format = responseFormat?.Trim().ToLowerInvariant();

        if (IsPlainTextResponse(format, mediaType))
        {
            return new OpenAITranscriptionResponse
            {
                Text = raw,
            };
        }

        return format switch
        {
            "verbose_json" =>
                DeserializeRequired<OpenAITranscriptionVerboseResponse>(
                    raw,
                    "verbose transcription"),

            "diarized_json" =>
                DeserializeRequired<OpenAITranscriptionDiarizedResponse>(
                    raw,
                    "diarized transcription"),

            _ =>
                DeserializeRequired<OpenAITranscriptionResponse>(
                    raw,
                    "transcription")
        };
    }

    private static T DeserializeRequired<T>(
        string raw,
        string operationName)
    {
        return JsonSerializer.Deserialize<T>(
                   raw,
                   OpenAITranscriptionJsonOptions)
               ?? throw new InvalidOperationException(
                   $"OpenAI compatible {operationName} response was empty.");
    }

    private static bool IsPlainTextResponse(
        string? responseFormat,
        string? mediaType)
    {
        if (responseFormat is "text" or "srt" or "vtt")
            return true;

        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return mediaType.StartsWith(
            "text/",
            StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException
        CreateTranscriptionRequestException(
            string operationName,
            HttpResponseMessage response,
            string raw)
    {
        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(raw)
                ? $"{operationName} request failed " +
                  $"({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"{operationName} request failed " +
                  $"({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
    }

}
