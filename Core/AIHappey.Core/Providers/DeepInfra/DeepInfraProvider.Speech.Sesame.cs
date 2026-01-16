using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.DeepInfra;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<SpeechResponse> SesameSpeechRequest(SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var rootMetadata = request.GetSpeechProviderMetadata<DeepInfraSpeechProviderMetadata>(GetIdentifier());
        var metadata = rootMetadata?.Sesame;
        var outputFormat = request.OutputFormat ?? metadata?.ResponseFormat ?? "wav";
        var voicePresets = request.Voice ?? metadata?.PresetVoice;

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(request.Text))
            payload["text"] = request.Text;

        if (!string.IsNullOrEmpty(outputFormat))
            payload["response_format"] = outputFormat;

        if (voicePresets is not null)
            payload["preset_voice"] = voicePresets;

        if (!string.IsNullOrEmpty(rootMetadata?.ServiceTier))
            payload["service_tier"] = rootMetadata.ServiceTier;
        else
            payload["service_tier"] = "default";

        if (metadata is not null)
        {
            if (metadata.Temperature is not null)
                payload["temperature"] = metadata.Temperature;

            if (!string.IsNullOrEmpty(metadata.SpeakerAudio))
                payload["speaker_audio"] = metadata.SpeakerAudio;

            if (!string.IsNullOrEmpty(metadata.SpeakerTranscript))
                payload["speaker_transcript"] = metadata.SpeakerTranscript;

            if (metadata.MaxAudioLengthMs is not null)
                payload["max_audio_length_ms"] = metadata.MaxAudioLengthMs;

        }
        
        return await DeepInfraSpeechRequest(request.Model, payload, warnings, now, outputFormat, cancellationToken);
    }
}
