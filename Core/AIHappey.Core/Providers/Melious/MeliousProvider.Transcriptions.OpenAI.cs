using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.Melious;

public partial class MeliousProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateMeliousOpenAITranscriptionRequest(options);
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();

        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        await using var input = options.File.OpenReadStream();
        using var file = new StreamContent(input);
        if (!string.IsNullOrWhiteSpace(options.File.ContentType))
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(options.File.ContentType);

        form.Add(file, "file", string.IsNullOrWhiteSpace(options.File.FileName) ? "audio.bin" : options.File.FileName);
        form.Add(new StringContent(options.Model.Trim()), "model");
        form.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrWhiteSpace(options.Language))
            form.Add(new StringContent(options.Language.Trim()), "language");
        if (options.Temperature is not null)
            form.Add(new StringContent(options.Temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file", "model", "language", "response_format", "temperature", "stream"
        };
        foreach (var (name, value) in options.AdditionalProperties ?? [])
        {
            if (!reserved.Contains(name) && TryConvertMeliousFormScalar(value, out var scalar))
                form.Add(new StringContent(scalar), name);
        }

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Melious transcription failed ({(int)response.StatusCode}): {raw}");

        return ConvertMeliousOpenAITranscriptionResponse(raw, responseFormat);
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

    private static void ValidateMeliousOpenAITranscriptionRequest(OpenAITranscriptionRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field", nameof(options));
        if (options.File is null || options.File.Length == 0)
            throw new ArgumentException("'file' is a required field", nameof(options));

        var format = options.ResolveOpenAITranscriptionResponseFormat();
        if (format is not "json" and not "text" and not "srt" and not "vtt" and not "verbose_json")
            throw new NotSupportedException($"Melious transcription response_format '{format}' is not supported.");
        if (options.Temperature is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Melious transcription temperature must be between 0 and 1.");
        if (options.ChunkingStrategy is not null || options.Include?.Any() == true
            || options.KnownSpeakerNames?.Any() == true || options.KnownSpeakerReferences?.Any() == true
            || options.TimestampGranularities?.Any() == true)
        {
            throw new NotSupportedException("Melious does not document chunking, include, speaker, or timestamp_granularity transcription fields.");
        }
    }

    private static IOpenAITranscriptionResponse ConvertMeliousOpenAITranscriptionResponse(string raw, string responseFormat)
    {
        if (responseFormat is "text" or "srt" or "vtt")
            return new OpenAITranscriptionResponse { Text = raw };

        try
        {
            if (responseFormat == "verbose_json")
            {
                return JsonSerializer.Deserialize<OpenAITranscriptionVerboseResponse>(raw, JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException("Melious returned an empty verbose transcription response.");
            }

            return JsonSerializer.Deserialize<OpenAITranscriptionResponse>(raw, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("Melious returned an empty transcription response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Melious returned an invalid JSON transcription response.", exception);
        }
    }
}
