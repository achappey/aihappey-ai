using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;

namespace AIHappey.Core.AI;

public static class ModelProviderExtensions
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
            ?.Parts?.OfType<FileUIPart>()
                .Where(a => a.IsImage())
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

    public static async IAsyncEnumerable<UIMessagePart> StreamTranscriptionAsync(this IModelProvider modelProvider,
      ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inputFiles = chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.OfType<FileUIPart>()
                .Where(a => a.IsAudio())
                .Select(a => a.ToImageFile()) ?? [];

        foreach (var item in inputFiles)
        {
            var transcriptionRequest = new TranscriptionRequest
            {
                Model = chatRequest.Model,
                MediaType = item.MediaType,
                Audio = item.Data,
                ProviderOptions = chatRequest.ProviderMetadata
            };

            TranscriptionResponse? result = null;
            string? exceptionMessage = null;

            try
            {
                result = await modelProvider.TranscriptionRequest(transcriptionRequest, cancellationToken);
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

            var resultText = result?.Segments.Any() == true ?
                string.Join("\n\n", result.Segments.Select(a => $"{a.StartSecond} - {a.EndSecond}\n{a.Text}"))
                : result?.Text;

            if (!string.IsNullOrEmpty(resultText))
            {
                var messageId = Guid.NewGuid().ToString();
                yield return new TextStartUIMessageStreamPart
                {
                    Id = messageId
                };

                yield return new TextDeltaUIMessageStreamPart
                {
                    Delta = resultText,
                    Id = messageId
                };

                yield return new TextEndUIMessageStreamPart
                {
                    Id = messageId
                };
            }

            if (result?.Segments.Any() == true)
            {
                yield return new FileUIPart
                {
                    MediaType = "text/plain",
                    Url = Convert.ToBase64String(BinaryData.FromString(
                        string.Join("\n\n", result.Segments.Select(a => $"{a.StartSecond} - {a.EndSecond}\n{a.Text}"))
                    ))
                };
            }

            if (!string.IsNullOrEmpty(result?.Text))
                yield return new FileUIPart
                {
                    MediaType = "text/plain",
                    Url = Convert.ToBase64String(BinaryData.FromString(
                         result.Text
                     ))
                };
        }

        // 4. Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }
}
