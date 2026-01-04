using AIHappey.Core.AI;
using AIHappey.Common.Model;
using OpenAI.Audio;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var audioClient = new AudioClient(
               request.Model,
               GetKey()
           );

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var result = await audioClient.GenerateSpeechAsync(request.Text,
            GeneratedSpeechVoice.Alloy,
            new SpeechGenerationOptions()
            {
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
                Body = result.GetRawResponse().Content.ToString(),
            },
        };
    }
}