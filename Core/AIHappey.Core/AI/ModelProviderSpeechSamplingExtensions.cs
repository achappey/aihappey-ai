using AIHappey.Common.Model;
using ModelContextProtocol.Protocol;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.AI;

public static class ModelProviderSpeechSamplingExtensions
{
    public static async Task<CreateMessageResult> SpeechSamplingAsync(
        this IModelProvider modelProvider,
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
    {
        var input = string.Join("\n\n", chatRequest
            .Messages
            .Where(a => a.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(z => z.Content.OfType<TextContentBlock>())
            .Select(a => a.Text));

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new Exception("No prompt provided.");
        }

        var model = chatRequest.GetModel();

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new Exception("No model provided.");
        }

        var speechRequest = new SpeechRequest
        {
            Model = model,
            Text = input,
            Instructions = chatRequest.SystemPrompt
        };

        var result = await modelProvider.SpeechRequest(speechRequest, cancellationToken) ?? throw new Exception("No result.");

        return result.ToCreateMessageResult();
    }

    public static AudioContentBlock ToAudioContentBlock(
       this SpeechAudioResponse audio)
            => new()
            {
                MimeType = audio.MimeType,
                Data = audio.Base64
            };

    public static CreateMessageResult ToCreateMessageResult(
        this SpeechResponse result)
        => new()
        {
            Content =
            [
                result.Audio.ToAudioContentBlock()
            ],
            Model = result.Response.ModelId,
        };

}

