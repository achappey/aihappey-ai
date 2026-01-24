using System.Net.Mime;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Jina;

public static class JinaExtensions
{
    public static IEnumerable<object> ToJinaMessages(this IEnumerable<UIMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                Role.system => "system",
                Role.assistant => "assistant",
                Role.user => "user",
                _ => "user"
            };

            List<object> content = [];


            foreach (var part in msg.Parts)
            {
                if (part is TextUIPart textUIPart)
                {
                    content.Add(new { type = "text", text = textUIPart.Text });
                }
                else if (part is FileUIPart fileUIPart)
                {
                    if (fileUIPart.MediaType.StartsWith("image/"))
                    {
                        content.Add(new
                        {
                            type = "image",
                            image = fileUIPart.Url,
                            mimeType = fileUIPart.MediaType
                        });
                    }
                    else if (fileUIPart.MediaType.StartsWith(MediaTypeNames.Text.Plain)
                        || fileUIPart.MediaType.StartsWith(MediaTypeNames.Application.Json))
                    {
                        content.Add(new
                        {
                            type = "file",
                            data = fileUIPart.Url,
                            mimeType = fileUIPart.MediaType
                        });
                    }
                }
            }

            if (content.Count > 0)
                yield return new { role, content };
        }
    }
}
