using System.Text.Json;
using AIHappey.Common.Model.Providers.Noiz;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Noiz;

public partial class NoizProvider
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
        var metadata = request.GetProviderMetadata<NoizSpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceId) = ParseModelAndVoice(request.Model);

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Noiz speech model '{request.Model}' is not supported.");

        var voiceId = (modelVoiceId ?? request.Voice ?? metadata?.VoiceId)?.Trim();

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId)
                && !string.Equals(metadata.VoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.noiz.voice_id", reason = "voice is derived from model id" });
            }
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Text), "text");

        if (!string.IsNullOrWhiteSpace(voiceId))
            form.Add(new StringContent(voiceId), "voice_id");

        AddIfNotNull(form, "quality_preset", metadata?.QualityPreset);
        AddIfNotNull(form, "output_format", NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat));
        AddIfNotNull(form, "speed", request.Speed ?? metadata?.Speed);
        AddIfNotNull(form, "duration", metadata?.Duration);
        AddIfNotNull(form, "target_lang", request.Language ?? metadata?.TargetLang);
        AddIfNotNull(form, "similarity_enh", metadata?.SimilarityEnh);
        AddIfNotNull(form, "emo", metadata?.Emo);
        AddIfNotNull(form, "trim_silence", metadata?.TrimSilence);
        AddIfNotNull(form, "save_voice", metadata?.SaveVoice);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-speech")
        {
            Content = form
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Noiz TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(baseModelId, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(voiceId))
            providerMetadata["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web);
        if (metadata?.QualityPreset is not null)
            providerMetadata["quality_preset"] = JsonSerializer.SerializeToElement(metadata.QualityPreset.Value, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(request.OutputFormat ?? metadata?.OutputFormat))
            providerMetadata["output_format"] = JsonSerializer.SerializeToElement(NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat), JsonSerializerOptions.Web);
        if ((request.Speed ?? metadata?.Speed) is { } speed)
            providerMetadata["speed"] = JsonSerializer.SerializeToElement(speed, JsonSerializerOptions.Web);
        if (metadata?.Duration is not null)
            providerMetadata["duration"] = JsonSerializer.SerializeToElement(metadata.Duration.Value, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(request.Language ?? metadata?.TargetLang))
            providerMetadata["target_lang"] = JsonSerializer.SerializeToElement(request.Language ?? metadata?.TargetLang, JsonSerializerOptions.Web);
        if (metadata?.SimilarityEnh is not null)
            providerMetadata["similarity_enh"] = JsonSerializer.SerializeToElement(metadata.SimilarityEnh.Value, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(metadata?.Emo))
            providerMetadata["emo"] = JsonSerializer.SerializeToElement(metadata.Emo, JsonSerializerOptions.Web);
        if (metadata?.TrimSilence is not null)
            providerMetadata["trim_silence"] = JsonSerializer.SerializeToElement(metadata.TrimSilence.Value, JsonSerializerOptions.Web);
        if (metadata?.SaveVoice is not null)
            providerMetadata["save_voice"] = JsonSerializer.SerializeToElement(metadata.SaveVoice.Value, JsonSerializerOptions.Web);

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        var format = NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat) ?? GuessFormat(mediaType) ?? "wav";
        var mimeType = string.IsNullOrWhiteSpace(mediaType) ? GuessMimeType(format) : mediaType!;

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = "text-to-speech",
                    status = (int)resp.StatusCode,
                    contentType = mediaType,
                    headers = new
                    {
                        timestamp = GetHeader(resp, "X-Timestamp"),
                        audioDuration = GetHeader(resp, "X-Audio-Duration")
                    }
                }
            }
        };
    }

    private (string BaseModelId, string? VoiceId) ParseModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var slashIndex = raw.IndexOf('/');
        if (slashIndex < 0)
            return (raw, null);

        if (slashIndex == 0 || slashIndex >= raw.Length - 1)
            throw new ArgumentException("Noiz speech model must include both base model id and voice id in the form 'text-to-speech/{voiceId}'.", nameof(model));

        var baseModelId = raw[..slashIndex].Trim();
        var voiceId = raw[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Noiz speech model must include both base model id and voice id in the form 'text-to-speech/{voiceId}'.", nameof(model));

        return (baseModelId, voiceId);
    }

    private static void AddIfNotNull(MultipartFormDataContent form, string name, object? value)
    {
        if (value is null)
            return;

        var stringValue = value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        if (!string.IsNullOrWhiteSpace(stringValue))
            form.Add(new StringContent(stringValue), name);
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            var fmt => fmt
        };
    }

    private static string? GuessFormat(string? mediaType)
        => mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/mp3" => "mp3",
            "audio/wav" => "wav",
            "audio/x-wav" => "wav",
            _ => null
        };

    private static string GuessMimeType(string format)
        => string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
