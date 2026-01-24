using AIHappey.Common.Model.Providers.DeepInfra;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<SpeechResponse> ZyphraSpeechRequest(SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var rootMetadata = request.GetProviderMetadata<DeepInfraSpeechProviderMetadata>(GetIdentifier());
        var metadata = rootMetadata?.Zyphra;
        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "wav";
        var language = request.Language ?? metadata?.Language;

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(request.Text))
            payload["text"] = request.Text;

        if (!string.IsNullOrEmpty(outputFormat))
            payload["output_format"] = outputFormat;

        if (!string.IsNullOrEmpty(language))
            payload["language"] = language;

        if (!string.IsNullOrEmpty(rootMetadata?.ServiceTier))
            payload["service_tier"] = rootMetadata.ServiceTier;
        else
            payload["service_tier"] = "default";

        if (metadata is not null)
        {
            if (!string.IsNullOrEmpty(metadata.VoiceId))
                payload["voice_id"] = metadata.VoiceId;

            if (!string.IsNullOrEmpty(metadata.PresetVoice))
                payload["preset_voice"] = metadata.PresetVoice;

            if (metadata.SpeakerRate is not null)
                payload["speaker_rate"] = metadata.SpeakerRate;

            if (metadata.Seed is not null)
                payload["seed"] = metadata.Seed;
        }

        return await DeepInfraSpeechRequest(request.Model, payload, warnings, now, outputFormat, cancellationToken);
    }
}
