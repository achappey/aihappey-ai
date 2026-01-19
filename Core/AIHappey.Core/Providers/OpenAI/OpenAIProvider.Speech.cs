using AIHappey.Core.AI;
using AIHappey.Common.Model;
using OpenAI.Audio;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.ModelProviders;

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

        var formatString = request.OutputFormat ?? metadata?.ResponseFormat ?? "mp3";
        var format = new GeneratedSpeechFormat(formatString);

        var result = await audioClient.GenerateSpeechAsync(request.Text,
            voice,
            new SpeechGenerationOptions()
            {
                SpeedRatio = request.Speed ?? metadata?.Speed,
                Instructions = request.Instructions ?? metadata?.Instructions,
                ResponseFormat = format
            },
            cancellationToken);

        var base64 = Convert.ToBase64String(result.Value.ToArray());

        return new SpeechResponse()
        {
            Audio = new SpeechAudioResponse()
            {
                Base64 = base64,
                MimeType = MapToAudioMimeType(formatString),
                Format = formatString
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            },
        };
    }

    public static string MapToAudioMimeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "application/octet-stream";

        return value.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }


}
