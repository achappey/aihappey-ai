using AIHappey.Vercel.Extensions;
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

        ApplyAuthHeader();
        var payload = new { model, input = request.Text, voice };
        using var response = await _client.PostAsync("audio/speech",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Vultr speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        var mime = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = mime, Format = "wav" }, Warnings = warnings,
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

    private static (string Model, string? Voice) ParseSpeechShortcut(string model)
    {
        var local = StripVultrPrefix(model) ?? model;
        var split = local.LastIndexOf('/');
        return split > 0 && split < local.Length - 1
            ? (local[..split], local[(split + 1)..])
            : (local, null);
    }
}
