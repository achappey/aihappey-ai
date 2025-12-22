using OpenAI.Responses;
using OAIC = OpenAI.Chat;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public static class UIMessagePartConversionExtensions
{
    public static IEnumerable<ResponseContentPart> ToInputMessageResponseContentParts(this List<UIMessagePart> parts)
    {

        foreach (var part in parts)
        {
            switch (part)
            {
                case TextUIPart text:
                    yield return ResponseContentPart.CreateInputTextPart(text.Text);
                    break;
                case FileUIPart filePart:
                    var img = filePart.TryGetImageData();
                    if (img is not null)
                    {
                        yield return ResponseContentPart.CreateInputImagePart(
                            img,
                            filePart.MediaType,
                            ResponseImageDetailLevel.High
                        );
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported UIPart type: {part.GetType().Name}");
            }
        }
    }

    public static IEnumerable<OAIC.ChatMessageContentPart> ToInputMessageChatContentParts(this List<UIMessagePart> parts)
    {

        foreach (var part in parts)
        {
            switch (part)
            {
                case TextUIPart text:
                    yield return OAIC.ChatMessageContentPart.CreateTextPart(text.Text);
                    break;
                case FileUIPart filePart:
                    var img = filePart.TryGetImageData();
                    if (img is not null)
                    {
                        yield return OAIC.ChatMessageContentPart.CreateImagePart(
                            img,
                            filePart.MediaType,
                            OAIC.ChatImageDetailLevel.High
                        );
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported UIPart type: {part.GetType().Name}");
            }
        }
    }

    // 1.  Helper: convert only the TEXT assistant parts --------------------------
}