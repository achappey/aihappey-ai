using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.YourVoic;

public partial class YourVoicProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field", nameof(options));

        if (options.File is null || options.File.Length == 0)
            throw new ArgumentException("'file' is a required field", nameof(options));

        ApplyAuthHeader();

        var model = options.Model.Trim();
        var responseFormat = string.IsNullOrWhiteSpace(options.ResponseFormat)
            ? "json"
            : options.ResponseFormat.Trim().ToLowerInvariant();

        using var form = new MultipartFormDataContent();
        await using var input = options.File.OpenReadStream();
        var file = new StreamContent(input);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(options.File.ContentType)
                ? "application/octet-stream"
                : options.File.ContentType);

        form.Add(file, "file", string.IsNullOrWhiteSpace(options.File.FileName) ? "audio" : options.File.FileName);
        AddYourVoicFormValue(form, "model", model);
        AddYourVoicFormValue(form, "language", options.Language);

        var isCipher = model.StartsWith("cipher-", StringComparison.OrdinalIgnoreCase);
        var isLucid = model.StartsWith("lucid-", StringComparison.OrdinalIgnoreCase);

        // Cipher documents these fields. For unknown model families use the
        // universal endpoint and trust the backend to accept or reject them.
        if (isCipher || !isLucid)
        {
            AddYourVoicFormValue(form, "response_format", responseFormat);
            AddYourVoicFormValue(form, "prompt", options.Prompt);

            if (options.TimestampGranularities?.Length > 0)
                AddYourVoicFormValue(form, "timestamp_granularities", string.Join(',', options.TimestampGranularities));
        }

        var endpoint = isCipher
            ? "stt/cipher/transcribe"
            : isLucid
                ? "stt/lucid/transcribe"
                : "stt/transcribe";

        using var response = await _client.PostAsync(endpoint, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"YourVoic STT failed ({(int)response.StatusCode}): {body}");

        if (responseFormat is "text" or "srt" or "vtt")
            return new OpenAITranscriptionResponse { Text = body };

        return ConvertYourVoicOpenAITranscription(body, responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static void AddYourVoicFormValue(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static IOpenAITranscriptionResponse ConvertYourVoicOpenAITranscription(
        string body,
        string responseFormat)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var text = GetYourVoicString(root, "text") ?? string.Empty;

        if (responseFormat != "verbose_json")
            return new OpenAITranscriptionResponse { Text = text };

        return new OpenAITranscriptionVerboseResponse
        {
            Language = GetYourVoicString(root, "language") ?? string.Empty,
            Duration = GetYourVoicDouble(root, "duration"),
            Text = text,
            Segments = ReadYourVoicSegments(root),
            Words = ReadYourVoicWords(root),
            Usage = new OpenAITranscriptionDurationUsage
            {
                Seconds = GetYourVoicDouble(root, "duration")
            }
        };
    }

    private static OpenAITranscriptionSegment[]? ReadYourVoicSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        return values.EnumerateArray().Select((value, index) => new OpenAITranscriptionSegment
        {
            Id = index,
            Seek = 0,
            Start = GetYourVoicDouble(value, "start"),
            End = GetYourVoicDouble(value, "end"),
            Text = GetYourVoicString(value, "text") ?? string.Empty,
            Tokens = [],
            Temperature = 0,
            AverageLogprob = 0,
            CompressionRatio = 0,
            NoSpeechProbability = 0
        }).ToArray();
    }

    private static OpenAITranscriptionWord[]? ReadYourVoicWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        return values.EnumerateArray().Select(value => new OpenAITranscriptionWord
        {
            Word = GetYourVoicString(value, "word") ?? string.Empty,
            Start = GetYourVoicDouble(value, "start"),
            End = GetYourVoicDouble(value, "end")
        }).ToArray();
    }

    private static string? GetYourVoicString(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double GetYourVoicDouble(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDouble()
            : 0;
}

