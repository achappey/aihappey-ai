using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.AI;

public static class ModelProviderImageExtensions
{
    public static async IAsyncEnumerable<UIMessagePart> StreamImageAsync(this IModelProvider modelProvider,
      ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        var inputFiles = chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.GetImages()
                .Select(a => a.ToImageFile()) ?? [];

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();

            yield break;
        }

        // 2. Build ImageRequest
        var imageRequest = new ImageRequest
        {
            Prompt = prompt,
            Model = chatRequest.Model,
            Files = inputFiles,
            ProviderOptions = chatRequest.ProviderMetadata
        };

        ImageResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await modelProvider.ImageRequest(imageRequest, cancellationToken);
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

        foreach (var image in result?.Images ?? [])
        {
            var commaIndex = image.IndexOf(',');

            if (commaIndex <= 0)
                continue;

            var header = image[..commaIndex];              // data:image/png;base64
            var data = image[(commaIndex + 1)..];          // base64 payload

            var mediaType = header
                .Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);

            yield return new FileUIPart
            {
                MediaType = mediaType,   // "image/png"
                Url = data              // keep full data URL
            };
        }

        // 4. Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }

}
