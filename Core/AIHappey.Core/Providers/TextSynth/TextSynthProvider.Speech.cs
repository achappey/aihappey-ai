using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TextSynth;

public partial class TextSynthProvider
{
    private async Task<SpeechResponse> TextSynthSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var (baseModel, modelVoice) = ResolveSpeechModelAndVoice(request.Model);

        var candidateVoice = (modelVoice
            ?? request.Voice?.Trim()
            ?? TryGetString(metadata, "voice")?.Trim()
            ?? "Will").Trim();

        if (!TextSynthSpeechVoicesSet.Contains(candidateVoice))
            throw new NotSupportedException($"TextSynth speech voice '{candidateVoice}' is not supported.");

        var normalizedVoice = TextSynthSpeechVoices.First(v => string.Equals(v, candidateVoice, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(modelVoice))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            var metadataVoice = TryGetString(metadata, "voice");
            if (!string.IsNullOrWhiteSpace(metadataVoice)
                && !string.Equals(metadataVoice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.textsynth.voice", reason = "voice is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language)
            && !string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Language, "en-US", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Language, "en-GB", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "language", reason = "TextSynth speech currently supports English voices." });
        }

        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(request.OutputFormat, "mp3", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.OutputFormat, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "TextSynth speech endpoint returns MP3 stream." });
        }

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["voice"] = normalizedVoice,
            ["seed"] = TryGetInt(metadata, "seed")
        };

        var body = JsonSerializer.Serialize(payload, TextSynthJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/engines/{baseModel}/speech")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"TextSynth speech failed ({(int)response.StatusCode}): {errorBody}");
        }

        var mime = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mime))
            mime = "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = "mp3"
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = baseModel,
                    voice = normalizedVoice,
                    output_mime = mime
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    model = baseModel,
                    voice = normalizedVoice,
                    mime
                }
            }
        };
    }
}

