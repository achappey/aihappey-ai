using System.Net.Mime;
using AIHappey.Common.Model;
using Anthropic.SDK.Messaging;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static DocumentSource? ToDocumentSource(this FileUIPart part)
       => new()
       {
           Type = SourceType.base64,
           Data = part.Url[(part.Url.IndexOf(',') + 1)..],
           MediaType = part.MediaType
       };


    public static DocumentContent? ToDocumentContent(this FileUIPart part)
        => new()
        {
            Source = part.ToDocumentSource(),
            Title = part.Url,
            Citations = new Citations()
            {
                Enabled = true
            }
        };

    public static ContentBase? ToContentBase(this FileUIPart part)
      => part.MediaType switch
      {
          "image/png" or "image/jpeg" or "image/webp" => part.ToImageSource().ToImageContent(),
          MediaTypeNames.Application.Pdf => part.ToDocumentContent(),
          _ => null
      };


    public static ContentBase? ToContentBase(this UIMessagePart part)
        => part switch
        {
            TextUIPart text => text.Text.ToTextContent(),
            FileUIPart file => file.ToContentBase(),
            _ => null // default
        };


    public static ContentBase ToTextContent(this string value) =>
        new TextContent()
        {
            Text = value
        };

    public static ContentBase ToThinkingContent(this string value) =>
            new ThinkingContent()
            {
                Thinking = value,
            };

    public static ContentBase ToThinkingContent(this string value, string signature) =>
             new ThinkingContent()
             {
                 Thinking = value,
                 Signature = signature
             };

    public static ImageContent ToImageContent(this ImageSource imageSource) =>
        new()
        {
            Source = imageSource
        };


    public static Message ToMessage(this ContentBase contentBase, RoleType roleType)
               => new List<ContentBase>() { contentBase }.ToMessage(roleType);

    public static Message ToMessage(this List<ContentBase> contentBases, RoleType roleType)
           => new()
           {
               Role = roleType,
               Content = contentBases
           };


    public static string ToFinishReason(this string? to) =>
        to switch
        {
            "end_turn" => "stop",
            "stop_sequence" => "stop",
            "max_tokens" => "length",
            "model_context_window_exceeded" => "length",
            "tool_use" => "tool-call",
            _ => "stop"
        };

}
