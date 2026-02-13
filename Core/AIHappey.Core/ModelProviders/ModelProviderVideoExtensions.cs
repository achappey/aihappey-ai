using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderVideoExtensions
{
    public static async IAsyncEnumerable<UIMessagePart> StreamVideoAsync(this IModelProvider modelProvider,
      ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        var inputFiles = chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.GetImages()
                .Select(a => a.ToVideoFile()) ?? [];

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();

            yield break;
        }

        // 2. Build ImageRequest
        var imageRequest = new VideoRequest
        {
            Prompt = prompt,
            Model = chatRequest.Model,
            Image = inputFiles.FirstOrDefault(),
            ProviderOptions = chatRequest.ProviderMetadata
        };

        VideoResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await modelProvider.VideoRequest(imageRequest, cancellationToken);
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

        foreach (var image in result?.Videos ?? [])
        {
            yield return new FileUIPart
            {
                MediaType = image.MediaType,   // "image/png"
                Url = image.Data.ToDataUrl(image.MediaType)              // keep full data URL
            };
        }

        // 4. Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }

}
