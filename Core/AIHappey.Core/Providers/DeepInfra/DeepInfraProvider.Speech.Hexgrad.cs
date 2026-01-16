using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.DeepInfra;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<SpeechResponse> HexgradSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var rootMetadata = request.GetSpeechProviderMetadata<DeepInfraSpeechProviderMetadata>(GetIdentifier());
        var metadata = rootMetadata?.Hexgrad;
        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "wav";
        var speed = request.Speed ?? metadata?.Speed;
        var voicePresets = !string.IsNullOrEmpty(request.Voice) ? [request.Voice] : metadata?.PresetVoice;

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(request.Text))
            payload["text"] = request.Text;

        if (!string.IsNullOrEmpty(outputFormat))
            payload["output_format"] = outputFormat;

        if (speed is not null)
            payload["speed"] = speed;

        if (voicePresets is not null)
            payload["preset_voice"] = voicePresets;

        if (!string.IsNullOrEmpty(rootMetadata?.ServiceTier))
            payload["service_tier"] = rootMetadata.ServiceTier;
        else
            payload["service_tier"] = "default";

        if (metadata is not null)
        {
            if (metadata.ReturnTimestamps is not null)
                payload["return_timestamps"] = metadata.ReturnTimestamps;

            if (metadata.SampleRate is not null)
                payload["sample_rate"] = metadata.SampleRate;

            if (metadata.TargetMaxTokens is not null)
                payload["target_max_tokens"] = metadata.TargetMaxTokens;

            if (metadata.TargetMinTokens is not null)
                payload["target_min_tokens"] = metadata.TargetMinTokens;

            if (metadata.AbsoluteMaxTokens is not null)
                payload["absolute_max_tokens"] = metadata.AbsoluteMaxTokens;

        }

        return await DeepInfraSpeechRequest(request.Model, payload, warnings, now, outputFormat, cancellationToken);
    }
}
