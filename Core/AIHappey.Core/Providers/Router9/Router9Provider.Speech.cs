using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Router9;

public partial class Router9Provider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRouter9Speech(request.Model, request.Text, request.Voice);
        var payload = CreateRouter9Payload(request.ProviderOptions, "model", "input", "voice");
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        var result = await SynthesizeRouter9SpeechAsync(payload, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = ResolveRouter9AudioFormat(result.MimeType)
            },
            Warnings = GetRouter9SpeechWarnings(request.OutputFormat, request.Instructions, request.Speed, request.Language),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateRouter9Speech(options.Model, options.Input, options.Voice);
        var payload = CreateRouter9Payload(options.AdditionalProperties, "model", "input", "voice");
        payload["model"] = options.Model;
        payload["input"] = options.Input;
        if (!string.IsNullOrWhiteSpace(options.Voice)) payload["voice"] = options.Voice;
        var result = await SynthesizeRouter9SpeechAsync(payload, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<Router9SpeechResult> SynthesizeRouter9SpeechAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsJsonAsync("v1/audio/synthesize", payload, Router9JsonOptions, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Router9 speech synthesis failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(audio)}");
        if (audio.Length == 0) throw new InvalidOperationException("Router9 speech synthesis returned empty audio.");
        return new Router9SpeechResult(audio, response.Content.Headers.ContentType?.MediaType ?? "audio/wav", GetRouter9Headers(response));
    }

    private static void ValidateRouter9Speech(string model, string input, string? voice)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Input is required.", nameof(input));
        if (input.Length > 4096) throw new ArgumentException("Router9 speech input cannot exceed 4,096 characters.", nameof(input));
        if (!string.IsNullOrWhiteSpace(voice) && !Router9Voices.Contains(voice))
            throw new ArgumentException("Voice must be alloy, echo, fable, onyx, nova, or shimmer.", nameof(voice));
    }

    private static IEnumerable<object> GetRouter9SpeechWarnings(string? format, string? instructions, float? speed, string? language)
    {
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals("wav", StringComparison.OrdinalIgnoreCase)) yield return new { type = "unsupported", feature = "outputFormat" };
        if (!string.IsNullOrWhiteSpace(instructions)) yield return new { type = "unsupported", feature = "instructions" };
        if (speed is not null) yield return new { type = "unsupported", feature = "speed" };
        if (!string.IsNullOrWhiteSpace(language)) yield return new { type = "unsupported", feature = "language" };
    }

    private static string ResolveRouter9AudioFormat(string mimeType) => mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "wav" : mimeType.Split('/').Last();
    private static readonly HashSet<string> Router9Voices = new(["alloy", "echo", "fable", "onyx", "nova", "shimmer"], StringComparer.OrdinalIgnoreCase);
    private sealed record Router9SpeechResult(byte[] Audio, string MimeType, Dictionary<string, string> Headers);
}
