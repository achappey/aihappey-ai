using AIHappey.Common.Model.Providers.DeepInfra;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<SpeechResponse> CanopyLabsSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var rootMetadata = request.GetProviderMetadata<DeepInfraSpeechProviderMetadata>(GetIdentifier());
        var metadata = rootMetadata?.CanopyLabs;
        var outputFormat = request.OutputFormat ?? metadata?.ResponseFormat ?? "wav";
        var voicePresets = request.Voice ?? metadata?.Voice;

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(request.Text))
            payload["text"] = request.Text;

        if (!string.IsNullOrEmpty(outputFormat))
            payload["response_format"] = outputFormat;

        if (voicePresets is not null)
            payload["voice"] = voicePresets;

        if (!string.IsNullOrEmpty(rootMetadata?.ServiceTier))
            payload["service_tier"] = rootMetadata.ServiceTier;
        else
            payload["service_tier"] = "default";

        if (metadata is not null)
        {
            if (metadata.Temperature is not null)
                payload["temperature"] = metadata.Temperature;

            if (metadata.TopP is not null)
                payload["top_p"] = metadata.TopP;

            if (metadata.MaxTokens is not null)
                payload["max_tokens"] = metadata.MaxTokens;

            if (metadata.RepetitionPenalty is not null)
                payload["repetition_penalty"] = metadata.RepetitionPenalty;
        }

        return await DeepInfraSpeechRequest(request.Model, payload, warnings, now, outputFormat, cancellationToken);
    }
}
