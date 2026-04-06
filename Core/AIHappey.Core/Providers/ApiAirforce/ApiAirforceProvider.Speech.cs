using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    private async Task<SpeechResponse> SpeechRequestApiAirforce(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var model = NormalizeModelId(request.Model);
        if (!model.StartsWith("suno-", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"ApiAirforce speech is currently implemented only for Suno models. Received '{request.Model}'.");

        var now = DateTime.UtcNow;
        var warnings = BuildSpeechWarnings(request);
        var providerOptions = TryGetProviderOptions(request.ProviderOptions, GetIdentifier());
        var responseFormat = ResolveResponseFormat(providerOptions, "url")!;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "prompt", "response_format"
        };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Text,
            ["response_format"] = responseFormat
        };

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        payload["model"] = model;
        payload["prompt"] = request.Text;
        payload["response_format"] = responseFormat;

        var root = await SendMediaGenerationAsync(payload, cancellationToken);
        var audio = await ExtractAudioAsync(root, ResolveAudioFormat(request.OutputFormat), cancellationToken);

        return new SpeechResponse
        {
            Audio = audio,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static List<object> BuildSpeechWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            AddUnsupportedWarning(warnings, "voice", "Suno music generation does not use the standard speech voice parameter.");

        if (request.Speed is not null)
            AddUnsupportedWarning(warnings, "speed");

        if (!string.IsNullOrWhiteSpace(request.Language))
            AddUnsupportedWarning(warnings, "language");

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            AddUnsupportedWarning(warnings, "instructions", "Use providerOptions.apiairforce.style or custom Suno parameters instead.");

        return warnings;
    }

    private async Task<SpeechAudioResponse> ExtractAudioAsync(JsonElement root, string fallbackFormat, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ApiAirforce speech generation returned no data array.");

        foreach (var item in dataEl.EnumerateArray())
        {
            var base64 = TryGetString(item, "b64_json")
                ?? TryGetString(item, "audio_base64")
                ?? TryGetString(item, "base64")
                ?? TryGetString(item, "data");

            var format = ResolveAudioFormat(
                TryGetString(item, "format")
                ?? TryGetString(item, "audio_format")
                ?? fallbackFormat);

            if (!string.IsNullOrWhiteSpace(base64))
            {
                return new SpeechAudioResponse
                {
                    Base64 = base64,
                    MimeType = ResolveAudioMimeType(format),
                    Format = format
                };
            }

            var url = TryGetString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await TryFetchAsBase64Async(url, cancellationToken);
            if (downloaded is not null)
            {
                return new SpeechAudioResponse
                {
                    Base64 = downloaded.Value.Base64,
                    MimeType = downloaded.Value.MediaType,
                    Format = ResolveAudioFormat(format, Path.GetExtension(url).Trim('.'))
                };
            }

            var inferredMediaType = GuessMediaTypeFromUrl(url, ResolveAudioMimeType(format));
            return new SpeechAudioResponse
            {
                Base64 = url,
                MimeType = inferredMediaType,
                Format = ResolveAudioFormat(format)
            };
        }

        throw new InvalidOperationException("ApiAirforce speech generation returned no audio output.");
    }
}
