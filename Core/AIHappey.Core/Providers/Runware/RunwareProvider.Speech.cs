using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var generatedAt = DateTime.UtcNow;
        var (audio, mimeType, payload) = await ExecuteAudioInferenceAsync(
            request.Model,
            request.Text,
            cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = ResolveSpeechFormat(mimeType)
            },
            Warnings = BuildSpeechWarnings(request),
            Request = new SpeechRequestItem { Body = new[] { payload } },
            Response = new ResponseData
            {
                Timestamp = generatedAt,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Runware does not document audioInference streaming. Adapt the completed response to
        // OpenAI's event shape without claiming chunked provider delivery.
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<(byte[] Audio, string MimeType, Dictionary<string, object?> Payload)> ExecuteAudioInferenceAsync(
        string model,
        string text,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["taskType"] = "audioInference",
            ["taskUUID"] = Guid.NewGuid().ToString(),
            ["model"] = model,
            ["speech"] = new Dictionary<string, object?> { ["text"] = text }
        };

        var json = JsonSerializer.Serialize(new[] { payload }, JsonOpts);
        using var response = await _client.PostAsync(
            string.Empty,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runware audio inference failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        ThrowRunwareAudioErrors(root);

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Runware audio inference response did not include a data array.");

        foreach (var item in data.EnumerateArray())
        {
            if (TryReadInlineAudio(item, out var inlineAudio, out var inlineMimeType))
                return (inlineAudio, inlineMimeType, payload);

            if (item.TryGetProperty("audioURL", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                && Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var audioUri))
            {
                using var audioResponse = await _client.GetAsync(audioUri, cancellationToken);
                var bytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!audioResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Runware audio download failed ({(int)audioResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

                var mimeType = audioResponse.Content.Headers.ContentType?.MediaType
                    ?? ResolveSpeechMimeType(audioUri.AbsolutePath);
                return (bytes, mimeType, payload);
            }
        }

        throw new InvalidOperationException("Runware audio inference returned no audio.");
    }

    private static void ThrowRunwareAudioErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
            return;

        var messages = errors.EnumerateArray()
            .Select(error => error.TryGetProperty("message", out var message)
                ? message.GetString()
                : error.GetRawText())
            .Where(message => !string.IsNullOrWhiteSpace(message));
        throw new InvalidOperationException($"Runware audio inference failed: {string.Join("; ", messages)}");
    }

    private static bool TryReadInlineAudio(JsonElement item, out byte[] audio, out string mimeType)
    {
        audio = [];
        mimeType = "application/octet-stream";

        if (item.TryGetProperty("audioDataURI", out var dataUriElement)
            && dataUriElement.ValueKind == JsonValueKind.String
            && TryDecodeDataUri(dataUriElement.GetString(), out audio, out mimeType))
            return true;

        if (item.TryGetProperty("audioBase64Data", out var base64Element)
            && base64Element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(base64Element.GetString()))
        {
            audio = Convert.FromBase64String(base64Element.GetString()!);
            mimeType = item.TryGetProperty("mimeType", out var mimeElement)
                && mimeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mimeElement.GetString())
                    ? mimeElement.GetString()!
                    : "audio/mpeg";
            return true;
        }

        return false;
    }

    private static bool TryDecodeDataUri(string? value, out byte[] audio, out string mimeType)
    {
        audio = [];
        mimeType = "application/octet-stream";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = value.IndexOf(',');
        var semicolon = value.IndexOf(';');
        if (comma < 0 || semicolon <= "data:".Length || semicolon > comma)
            return false;

        mimeType = value["data:".Length..semicolon];
        audio = Convert.FromBase64String(value[(comma + 1)..]);
        return true;
    }

    private static IEnumerable<object> BuildSpeechWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();
        AddUnsupportedSpeechWarning(warnings, "voice", request.Voice);
        AddUnsupportedSpeechWarning(warnings, "outputFormat", request.OutputFormat);
        AddUnsupportedSpeechWarning(warnings, "instructions", request.Instructions);
        AddUnsupportedSpeechWarning(warnings, "speed", request.Speed);
        AddUnsupportedSpeechWarning(warnings, "language", request.Language);
        if (request.ProviderOptions?.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "providerOptions" });
        return warnings;
    }

    private static void AddUnsupportedSpeechWarning(List<object> warnings, string feature, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            warnings.Add(new { type = "unsupported", feature });
    }

    private static string ResolveSpeechMimeType(string value)
        => Path.GetExtension(value).TrimStart('.').ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "ogg" or "oga" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "m4a" => "audio/mp4",
            _ => "audio/mpeg"
        };

    private static string ResolveSpeechFormat(string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            "audio/mp4" => "m4a",
            _ => "mp3"
        };
}


