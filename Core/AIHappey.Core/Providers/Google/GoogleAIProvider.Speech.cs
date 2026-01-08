using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using Mscc.GenerativeAI;
using AIHappey.Common.Model.Providers.Google;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
    : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var googleAI = GetClient();
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetSpeechProviderMetadata<GoogleSpeechProviderMetadata>(GetIdentifier());

        var ttsModel =
            metadata?.TtsModel
            ?? request.Model
            ?? "gemini-2.5-flash-preview-tts";

        var voice =
            request.Voice
            ?? metadata?.Voice
            ?? "Kore";

        var speechConfig = new SpeechConfig();

        var speakers = metadata?.Speakers?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (speakers is { Count: > 0 })
        {
            speechConfig.MultiSpeakerVoiceConfig = new MultiSpeakerVoiceConfig
            {
                SpeakerVoiceConfigs =
                [
                    .. speakers.Select(s => new SpeakerVoiceConfig
                    {
                        Speaker = s.Name!,
                        VoiceConfig = new VoiceConfig
                        {
                            PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                            {
                                VoiceName = string.IsNullOrWhiteSpace(s.Voice) ? voice : s.Voice
                            }
                        }
                    })
                ]
            };
        }
        else
        {
            speechConfig.VoiceConfig = new VoiceConfig
            {
                PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                {
                    VoiceName = voice
                }
            };
        }

        var modelClient = googleAI.GenerativeModel(ttsModel);
        var item = await modelClient.GenerateContent(new GenerateContentRequest
        {
            Model = ttsModel,
            Contents = [new Content(request.Text)],
            GenerationConfig = new()
            {
                ResponseModalities = [ResponseModality.Audio],
                SpeechConfig = speechConfig,
                Seed = metadata?.Seed
            }
        }, cancellationToken: cancellationToken);

        var audioPart = item.Candidates
            ?.FirstOrDefault()
            ?.Content
            ?.Parts
            ?.FirstOrDefault(p => p?.InlineData is not null);

        var base64 = audioPart?.InlineData?.Data;
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("No audio data returned.");

        var mime = audioPart?.InlineData?.MimeType;
        if (string.IsNullOrWhiteSpace(mime))
            mime = "audio/L16";

        return new SpeechResponse
        {
            Audio = base64.ToDataUrl(mime),
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = ttsModel,
            }
        };
    }
}

