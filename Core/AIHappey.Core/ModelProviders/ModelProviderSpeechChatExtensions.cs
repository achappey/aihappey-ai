using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderSpeechChatExtensions
{
    public static async IAsyncEnumerable<UIMessagePart> StreamSpeechAsync(
        this IModelProvider modelProvider,
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        var speechRequest = new SpeechRequest
        {
            Model = chatRequest.Model,
            Text = prompt,
            ProviderOptions = chatRequest.ProviderMetadata,
        };

        SpeechResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await modelProvider.SpeechRequest(speechRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            yield return exceptionMessage.ToErrorUIPart();
            yield break;
        }

        //var audio = result?.Audio as string;
        if (string.IsNullOrWhiteSpace(result?.Audio?.Base64))
        {
            yield return "Provider returned no audio.".ToErrorUIPart();
            yield break;
        }

        var mimeType = "audio/mpeg";
        var base64 = result.Audio.Base64;

        yield return new FileUIPart
        {
            MediaType = mimeType,
            Url = base64.ToDataUrl(mimeType)
        };

        // Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }
}

