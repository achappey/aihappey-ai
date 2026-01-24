using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    private async Task<SpeechResponse> DialogueRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.GetProviderMetadata<ElevenLabsSpeechProviderMetadata>(GetIdentifier());

        // output_format is a query parameter for /v1/text-to-dialogue
        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "mp3_44100_128";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(outputFormat))
            query.Add($"output_format={Uri.EscapeDataString(outputFormat)}");

        var url = "v1/text-to-dialogue" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        // Model-id mapping: incoming is typically "elevenlabs/<modelId>/text-to-dialogue".
        // We must send base model_id without provider prefix (if present) and without the suffix.
        var model = request.Model;
        var prefix = GetIdentifier() + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model.SplitModelId().Model;
        if (model.EndsWith("/text-to-dialogue", StringComparison.OrdinalIgnoreCase))
            model = model[..^"/text-to-dialogue".Length];

        var inputs = metadata?.Dialogue?.Inputs?.ToList() ?? new List<ElevenLabsSpeechDialogueInput>();
        if (inputs.Count == 0)
            throw new ArgumentException("ElevenLabs Text-to-Dialogue requires providerOptions.elevenlabs.dialogue.inputs.", nameof(request));

        foreach (var i in inputs)
        {
            if (i is null)
                throw new ArgumentException("Dialogue input item cannot be null.", nameof(request));
            if (string.IsNullOrWhiteSpace(i.VoiceId))
                throw new ArgumentException("Each dialogue input must include voice_id.", nameof(request));
            if (string.IsNullOrWhiteSpace(i.Text))
                throw new ArgumentException("Each dialogue input must include text.", nameof(request));
        }

        var warnings = new List<object>();
        // Explicitly ignored/unsupported in dialogue mode.
        if (!string.IsNullOrWhiteSpace(request.Text))
            warnings.Add(new { type = "ignored", feature = "text" });
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "ignored", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var body = new Dictionary<string, object?>
        {
            ["inputs"] = inputs,
            ["model_id"] = model,
        };

        var languageCode = request.Language ?? metadata?.LanguageCode;
        if (!string.IsNullOrWhiteSpace(languageCode))
            body["language_code"] = languageCode;

        if (metadata?.Dialogue?.Settings is not null)
            body["settings"] = metadata.Dialogue.Settings;

        if (metadata?.PronunciationDictionaryLocators is not null)
            body["pronunciation_dictionary_locators"] = metadata.PronunciationDictionaryLocators;

        if (metadata?.Seed is not null)
            body["seed"] = metadata.Seed.Value;

        if (!string.IsNullOrWhiteSpace(metadata?.ApplyTextNormalization))
            body["apply_text_normalization"] = metadata.ApplyTextNormalization;

        using var resp = await _client.PostAsJsonAsync(url, body, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs Text-to-Dialogue failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = GuessMimeType(outputFormat);
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = outputFormat?.Split("_")?.FirstOrDefault() ?? "mp3",
            },
            Warnings = warnings,
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model }
        };
    }

}

