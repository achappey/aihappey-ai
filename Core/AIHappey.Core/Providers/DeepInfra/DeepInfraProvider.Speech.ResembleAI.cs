using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.DeepInfra;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<SpeechResponse> ResembleAISpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var rootMetadata = request.GetSpeechProviderMetadata<DeepInfraSpeechProviderMetadata>(GetIdentifier());
        var metadata = rootMetadata?.ResembleAI;
        var outputFormat = request.OutputFormat ?? metadata?.ResponseFormat ?? "wav";
        var voiceId = request.Voice ?? metadata?.VoiceId;
        var languageId = request.Language ?? metadata?.LanguageId;

        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(request.Text))
            payload["text"] = request.Text;

        if (!string.IsNullOrEmpty(outputFormat))
            payload["response_format"] = outputFormat;

        if (!string.IsNullOrEmpty(voiceId))
            payload["voice_id"] = voiceId;

        if (!string.IsNullOrEmpty(languageId))
            payload["language_id"] = languageId;

        if (!string.IsNullOrEmpty(rootMetadata?.ServiceTier))
            payload["service_tier"] = rootMetadata.ServiceTier;
        else
            payload["service_tier"] = "default";

        if (metadata is not null)
        {

            if (metadata.Exaggeration is not null)
                payload["exaggeration"] = metadata.Exaggeration;

            if (metadata.Cfg is not null)
                payload["cfg"] = metadata.Cfg;

            if (metadata.Temperature is not null)
                payload["temperature"] = metadata.Temperature;

            if (metadata.Seed is not null)
                payload["seed"] = metadata.Seed;

            if (metadata.TopP is not null)
                payload["top_p"] = metadata.TopP;

            if (metadata.MinP is not null)
                payload["min_p"] = metadata.MinP;

            if (metadata.RepetitionPenalty is not null)
                payload["repetition_penalty"] = metadata.RepetitionPenalty;

            if (metadata.TopK is not null)
                payload["top_k"] = metadata.TopK;

        }

        return await DeepInfraSpeechRequest(request.Model, payload, warnings, now, outputFormat, cancellationToken);

    }
}
