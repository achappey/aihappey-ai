using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private const int OpenRouterSpeechStreamBufferSize = 16 * 1024;

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (speechRequest, warnings) = PrepareOpenRouterStreamingSpeech(request);
        var payload = BuildOpenRouterSpeechPayload(speechRequest);
        var responseFormat = NormalizeOpenRouterSpeechFormat(
            ReadOpenRouterSpeechString(payload.GetValueOrDefault("response_format")))
            ?? throw new NotSupportedException("OpenRouter speech supports only the 'mp3' and 'pcm' output formats.");
        payload["response_format"] = responseFormat;

        var input = ReadOpenRouterSpeechString(payload.GetValueOrDefault("input")) ?? speechRequest.Text;

        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenRouterSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"OpenRouter speech request failed ({(int)response.StatusCode})."
                : $"OpenRouter speech request failed ({(int)response.StatusCode}): {error}");
        }

        var contentType = response.Content.Headers.ContentType;
        if (contentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenRouter speech request returned JSON instead of audio: {body}");
        }

        var (mimeType, rate, channels) = ReadOpenRouterSpeechContentType(contentType, responseFormat);
        var audioFormat = new AudioFormat { Type = mimeType, Rate = rate };
        var generationId = ReadOpenRouterGenerationId(response);

        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = speechRequest.Model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        // Keep raw PCM chunks aligned to whole 16-bit sample frames so each chunk is independently playable.
        var frameSize = responseFormat == "pcm" ? 2 * Math.Max(channels ?? 1, 1) : 1;
        var buffer = new byte[OpenRouterSpeechStreamBufferSize];
        var pending = 0;
        long totalBytes = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(pending), cancellationToken);
            if (read == 0)
                break;

            totalBytes += read;
            var available = pending + read;
            var emit = available - available % frameSize;
            pending = available - emit;

            if (emit == 0)
                continue;

            var audio = Convert.ToBase64String(buffer, 0, emit);
            if (pending > 0)
                Buffer.BlockCopy(buffer, emit, buffer, 0, pending);

            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = audio };

            yield return new SpeechAudioPart { Audio = audio, AudioFormat = audioFormat };
        }

        if (pending > 0)
        {
            var audio = Convert.ToBase64String(buffer, 0, pending);

            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = audio };

            yield return new SpeechAudioPart { Audio = audio, AudioFormat = audioFormat };
        }

        cancellationToken.ThrowIfCancellationRequested();

        yield return new SpeechFinishPart
        {
            Usage = new StreamingSpeechUsage
            {
                InputCharacters = input.EnumerateRunes().Count()
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                generationId,
                contentType = contentType?.ToString(),
                responseFormat,
                audioBytes = totalBytes
            })
        };
    }

    private static (SpeechRequest Request, List<object> Warnings) PrepareOpenRouterStreamingSpeech(StreamingSpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));

        if (request.Text.Any(part => part is null || part.Type != "text" || part.Text is null))
            throw new ArgumentException("Speech parts must have type 'text' and non-null text.", nameof(request));

        if (request.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0))
            throw new ArgumentOutOfRangeException(nameof(request), "Speech speed must be a positive number.");

        if (request.OutputFormat is not null && NormalizeOpenRouterSpeechFormat(request.OutputFormat.Type) is null)
            throw new NotSupportedException(
                $"OpenRouter speech output format '{request.OutputFormat.Type}' is not supported. Use 'mp3' or 'pcm'.");

        var voices = request.Text
            .Select(part => string.IsNullOrWhiteSpace(part.Voice) ? request.Voice : part.Voice)
            .Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Select(voice => voice!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (voices.Length > 1)
            throw new NotSupportedException("OpenRouter speech accepts one voice per request.");

        var text = string.Concat(request.Text.Select(part => part.Text));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Speech text is required.", nameof(request));

        var warnings = new List<object>();

        if (request.OutputFormat?.Rate is not null)
            warnings.Add(new { type = "unsupported", feature = "outputFormat.rate" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var speechRequest = new SpeechRequest
        {
            Model = request.Model,
            Text = text,
            Voice = voices.SingleOrDefault(),
            OutputFormat = NormalizeOpenRouterSpeechFormat(request.OutputFormat?.Type),
            Speed = request.Speed,
            ProviderOptions = MergeOpenRouterStreamingSpeechOptions(request)
        };

        return (speechRequest, warnings);
    }

    private static Dictionary<string, JsonElement>? MergeOpenRouterStreamingSpeechOptions(StreamingSpeechRequest request)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in ReadOpenRouterSpeechOptions(request.ProviderOptions))
            merged[property.Key] = property.Value;

        // All text parts are synthesized in one HTTP request, so part-level options must agree.
        Dictionary<string, JsonElement>? partOptions = null;
        foreach (var part in request.Text)
        {
            var current = ReadOpenRouterSpeechOptions(part.ProviderOptions);

            if (partOptions is not null && !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(partOptions),
                    JsonSerializer.SerializeToElement(current)))
            {
                throw new NotSupportedException(
                    "OpenRouter speech requires the same provider options for every text part.");
            }

            partOptions ??= current;
        }

        foreach (var property in partOptions ?? [])
            merged[property.Key] = property.Value;

        return merged.Count == 0
            ? null
            : new Dictionary<string, JsonElement> { ["openrouter"] = JsonSerializer.SerializeToElement(merged) };
    }

    private static Dictionary<string, JsonElement> ReadOpenRouterSpeechOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue("openrouter", out var options)
            || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new(StringComparer.Ordinal);
        }

        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.openrouter must be an object.", nameof(providerOptions));

        return options.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string? NormalizeOpenRouterSpeechFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        return format.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "mp3" or "mpeg" or "audio/mpeg" or "audio/mp3" => "mp3",
            "pcm" or "audio/pcm" => "pcm",
            _ => null
        };
    }

    private static (string MimeType, int? Rate, int? Channels) ReadOpenRouterSpeechContentType(
        MediaTypeHeaderValue? contentType,
        string responseFormat)
    {
        var mimeType = string.IsNullOrWhiteSpace(contentType?.MediaType)
            || contentType.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? ResolveOpenRouterSpeechMimeType(responseFormat)
            : contentType.MediaType.ToLowerInvariant();

        return (mimeType,
            ReadOpenRouterContentTypeInt(contentType, "rate"),
            ReadOpenRouterContentTypeInt(contentType, "channels"));
    }

    private static int? ReadOpenRouterContentTypeInt(MediaTypeHeaderValue? contentType, string name)
    {
        var value = contentType?.Parameters
            .FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim('"');

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static string? ReadOpenRouterGenerationId(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-Generation-Id", out var values)
            ? values.FirstOrDefault()
            : null;
}
