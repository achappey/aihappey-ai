using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<MistralSpeechProviderMetadata>(GetIdentifier());
        var selection = await ResolveSpeechSelectionAsync(request, metadata, warnings, cancellationToken);
        var responseFormat = NormalizeSpeechResponseFormat(request.OutputFormat ?? metadata?.ResponseFormat);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["model"] = selection.ModelId,
            ["stream"] = false
        };

        if (!string.IsNullOrWhiteSpace(selection.VoiceId))
            payload["voice_id"] = selection.VoiceId;

        if (!string.IsNullOrWhiteSpace(responseFormat))
            payload["response_format"] = responseFormat;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{GetName()} speech request failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var audioBase64 = root.TryGetString("audio_data");

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"{GetName()} speech response did not contain 'audio_data'. Body: {body}");

        var resolvedFormat = NormalizeSpeechResponseFormat(root.TryGetString("audio_format") ?? responseFormat) ?? "mp3";
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(selection.ModelId, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(selection.VoiceId))
            providerMetadata["voice_id"] = JsonSerializer.SerializeToElement(selection.VoiceId, JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(selection.VoiceSlug))
            providerMetadata["voice_slug"] = JsonSerializer.SerializeToElement(selection.VoiceSlug, JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(resolvedFormat))
            providerMetadata["response_format"] = JsonSerializer.SerializeToElement(resolvedFormat, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = audioBase64!,
                MimeType = ResolveSpeechMimeType(resolvedFormat),
                Format = resolvedFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private async Task<MistralSpeechSelection> ResolveSpeechSelectionAsync(
        SpeechRequest request,
        MistralSpeechProviderMetadata? metadata,
        ICollection<object> warnings,
        CancellationToken cancellationToken)
    {
        var (modelId, modelVoiceSlug) = ParseSpeechModelAndVoiceSlug(request.Model);
        if (!string.IsNullOrWhiteSpace(modelVoiceSlug))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceSlug, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId)
                && !string.Equals(metadata.VoiceId.Trim(), modelVoiceSlug, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.mistral.voice_id", reason = "voice is derived from model id" });
            }

            var voice = await ResolveVoiceBySlugAsync(modelVoiceSlug!, cancellationToken)
                ?? throw new NotSupportedException($"{GetName()} voice slug '{modelVoiceSlug}' is not available.");

            return new MistralSpeechSelection(modelId, voice.Id, voice.Slug);
        }

        var requestedVoice = metadata?.VoiceId ?? request.Voice;
        if (string.IsNullOrWhiteSpace(requestedVoice))
            return new MistralSpeechSelection(modelId, null, null);

        var directVoice = requestedVoice.Trim();
        var resolvedBySlug = await ResolveVoiceBySlugAsync(directVoice, cancellationToken);
        return resolvedBySlug is null
            ? new MistralSpeechSelection(modelId, directVoice, null)
            : new MistralSpeechSelection(modelId, resolvedBySlug.Id, resolvedBySlug.Slug);
    }

    private static (string ModelId, string? VoiceSlug) ParseSpeechModelAndVoiceSlug(string model)
    {
        var localModel = model.Trim();
        const string providerPrefix = "mistral/";
        if (localModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            localModel = localModel[providerPrefix.Length..];

        var slashIndex = localModel.LastIndexOf('/');
        if (slashIndex <= 0 || slashIndex >= localModel.Length - 1)
            return (localModel, null);

        var baseModel = localModel[..slashIndex].Trim();
        var voiceSlug = localModel[(slashIndex + 1)..].Trim();

        if (!baseModel.Contains("tts", StringComparison.OrdinalIgnoreCase))
            return (localModel, null);

        if (string.IsNullOrWhiteSpace(voiceSlug))
            throw new ArgumentException("Mistral speech model voice shortcut must be in the form '[tts-model]/{voice-slug}'.", nameof(model));

        return (baseModel, voiceSlug);
    }

    private static string? NormalizeSpeechResponseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        return format.Trim().ToLowerInvariant() switch
        {
            "wave" => "wav",
            "mp3" => "mp3",
            "wav" => "wav",
            "flac" => "flac",
            "opus" => "opus",
            "pcm" => "pcm",
            var unknown => unknown
        };
    }

    private static string ResolveSpeechMimeType(string format)
        => format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "opus" => "audio/opus",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };

    private sealed record MistralSpeechSelection(string ModelId, string? VoiceId, string? VoiceSlug);


}
