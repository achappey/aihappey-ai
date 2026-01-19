using AIHappey.Common.Model;
using AIHappey.Core.AI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<SpeechResponse> SpeechRequest(
      SpeechRequest request,
      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = string.IsNullOrWhiteSpace(request.Model)
            ? "text-to-speech"
            : request.Model.Contains('/')
                ? request.Model.SplitModelId().Model
                : request.Model;

        var now = DateTime.UtcNow;
        var config = SpeechConfig.FromSubscription(GetKey(), GetEndpointRegion());

        if (!string.IsNullOrWhiteSpace(request.Voice))
            config.SpeechSynthesisVoiceName = request.Voice;

        if (!string.IsNullOrWhiteSpace(request.Language))
            config.SpeechSynthesisLanguage = request.Language;

        // Default: WAV PCM 16kHz 16bit mono (Azure default)
        using var pullStream = AudioOutputStream.CreatePullStream()
            ?? throw new InvalidOperationException("Failed to create PullAudioOutputStream.");

        using var audioConfig = AudioConfig.FromStreamOutput(pullStream);
        using var synthesizer = new SpeechSynthesizer(config, audioConfig);

        var result = await synthesizer
            .SpeakTextAsync(request.Text)
            .WaitAsync(cancellationToken);

        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
        {
            return new SpeechResponse
            {
                Audio = new SpeechAudioResponse()
                {
                    Base64 = Convert.ToBase64String(result.AudioData),
                    MimeType = "audio/wav",
                    Format = "wav",
                },
                Response = new()
                {
                    Timestamp = now,
                    ModelId = modelId
                }
            };
        }

        throw new Exception(Enum.GetName(result.Reason));
    }
}

