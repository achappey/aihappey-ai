using AIHappey.Core.AI;
using AIHappey.Common.Model;
using OpenAI.Audio;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var audioClient = new AudioClient(
               request.Model,
               GetKey()
           );

        var metadata = request.GetSpeechProviderMetadata<OpenAiSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var voice = !string.IsNullOrEmpty(request.Voice) ? new GeneratedSpeechVoice(request.Voice)
            : !string.IsNullOrEmpty(metadata?.Voice)
            ? new GeneratedSpeechVoice(metadata?.Voice) : GeneratedSpeechVoice.Alloy;

        var format = !string.IsNullOrEmpty(request.OutputFormat) ? new GeneratedSpeechFormat(request.OutputFormat)
                  : !string.IsNullOrEmpty(metadata?.ResponseFormat)
            ? new GeneratedSpeechFormat(metadata?.ResponseFormat) : GeneratedSpeechFormat.Mp3;

        var result = await audioClient.GenerateSpeechAsync(request.Text,
            voice,
            new SpeechGenerationOptions()
            {
                SpeedRatio = request.Speed ?? metadata?.Speed,
                Instructions = request.Instructions ?? metadata?.Instructions,
                ResponseFormat = format
            },
            cancellationToken);

        var base64 = Convert.ToBase64String(result.Value.ToArray()).ToDataUrl("audio/mpeg");

        return new SpeechResponse()
        {
            Audio = base64,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            },
        };
    }
}
