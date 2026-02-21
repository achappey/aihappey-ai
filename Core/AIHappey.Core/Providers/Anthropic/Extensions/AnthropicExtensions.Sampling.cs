using AIHappey.Vercel.Models;
using Anthropic.SDK.Messaging;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static ImageSource ToImageSource(this FileUIPart fileUIPart) =>
      new()
      {
          Data = fileUIPart.Url[(fileUIPart.Url.IndexOf(',') + 1)..],
          MediaType = fileUIPart.MediaType
      };

    public static Message ToMessage(this SamplingMessage samplingMessage)
    {
        List<ContentBase> contents = [];

        foreach (var content in samplingMessage.Content)
        {
            if (content is TextContentBlock textContentBlock)
            {
                contents.Add(textContentBlock.Text.ToTextContent());
            }

            if (content is ImageContentBlock imageContentBlock)
            {
                contents.Add(imageContentBlock.ToImageSource().ToImageContent());
            }
        }

        return new Message()
        {
            Role = samplingMessage.Role == ModelContextProtocol.Protocol.Role.User ? RoleType.User : RoleType.Assistant,
            Content = contents
        };
    }

    public static ImageSource ToImageSource(this ImageContentBlock imageContentBlock) =>
    new()
    {
        Data = Convert.ToBase64String(imageContentBlock.Data.ToArray()),
        MediaType = imageContentBlock.MimeType
    };

    public static IEnumerable<Message>
        ToMessages(this IList<SamplingMessage> samplingMessages)
            => samplingMessages.Select(a => a.ToMessage());
}