using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.", nameof(request));
        if (request.Text.Length > 2000) throw new ArgumentException("Vultr speech input is limited to 2,000 characters.", nameof(request));

        var (model, slugVoice) = ParseSpeechShortcut(request.Model);
        var voice = slugVoice ?? request.Voice;
        if (string.IsNullOrWhiteSpace(voice)) throw new ArgumentException("Voice is required.", nameof(request));
        var warnings = new List<object>();
        if (slugVoice is not null && !string.IsNullOrWhiteSpace(request.Voice) && !string.Equals(slugVoice, request.Voice, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null) warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) warnings.Add(new { type = "unsupported", feature = "outputFormat" });
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true)
        {
            if (providerOptions.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.vultr must be an object.", nameof(request));
            if (providerOptions.EnumerateObject().Any())
                throw new NotSupportedException("Vultr speech does not document provider-specific options.");
        }

        ApplyAuthHeader();
        var payload = new { model, input = request.Text, voice };
        using var response = await _client.PostAsync("v1/audio/speech",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Vultr speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        var mime = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
        var format = mime.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/ogg" => "ogg",
            _ => "wav"
        };
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = mime, Format = format },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { contentType = mime }),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }


    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var result = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return result.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        foreach (var item in result.ToOpenAISpeechStreamEvents()) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
    }

    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null || request.Text.Length == 0)
            throw new ArgumentException("At least one text part is required.", nameof(request));
        if (request.Text.Any(part => part is null || part.Text is null))
            throw new ArgumentException("Every speech text part must contain text.", nameof(request));
        if (request.Text.Any(part => part.ProviderOptions?.Count > 0))
            throw new NotSupportedException("Vultr speech does not document per-text-part options.");
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.vultr must be an object.", nameof(request));
            if (options.EnumerateObject().Any())
                throw new NotSupportedException("Vultr speech does not document provider-specific options.");
        }

        var voices = request.Text
            .Select(part => part.Voice ?? request.Voice)
            .Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (voices.Length > 1)
            throw new NotSupportedException("Vultr speech supports one voice per request.");

        var response = await SpeechRequest(new SpeechRequest
        {
            Model = request.Model,
            Text = string.Concat(request.Text.Select(part => part.Text)),
            Voice = voices.SingleOrDefault(),
            Instructions = request.Instructions,
            Speed = request.Speed,
            OutputFormat = request.OutputFormat?.Type
        }, cancellationToken);

        var audioFormat = response.Audio.Format;
        yield return new SpeechStreamStartPart { Warnings = response.Warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp,
            ModelId = response.Response.ModelId,
            Headers = response.Response.Headers
        };
        yield return new SpeechAudioPart
        {
            Audio = response.Audio.Base64,
            AudioFormat = new AudioFormat { Type = audioFormat },
            ProviderMetadata = response.ProviderMetadata
        };
        yield return new SpeechFinishPart
        {
            Usage = new StreamingSpeechUsage
            {
                InputCharacters = request.Text.Sum(part => (long)part.Text.EnumerateRunes().Count())
            },
            ProviderMetadata = response.ProviderMetadata
        };
    }

    private static (string Model, string? Voice) ParseSpeechShortcut(string model)
    {
        var local = model;
        var split = local.LastIndexOf('/');
        return split > 0 && split < local.Length - 1
            ? (local[..split], local[(split + 1)..])
            : (local, null);
    }
}
