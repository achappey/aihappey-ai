using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.AI;

public static class ModelContextExtensions
{
   public static async Task<CallToolResult?> WithExceptionCheck(this RequestContext<CallToolRequestParams> requestContext, Func<Task<CallToolResult?>> func)
   {
      try
      {
         return await func();
      }
      catch (Exception e)
      {
         return e.Message.ToErrorCallToolResponse();
      }
   }

   public static CallToolResult ToErrorCallToolResponse(this string content)
        => new()
        {
           IsError = true,
           Content = [content.ToTextContentBlock()]
        };

   public static string ToDataUrl(this ImageContentBlock imageContentBlock) => imageContentBlock.Data.ToDataUrl(imageContentBlock.MimeType);

   public static string GetToolName(this ToolInvocationPart tip) => tip.Type.Replace("tool-", string.Empty);

   public static string ExcludeMeta(this ToolInvocationPart tip)
   {
      try
      {
         var json = tip.Output.ToString();
         var typed = JsonSerializer.Deserialize<CallToolResult>(json!,
             JsonSerializerOptions.Web);

         if (typed!.Content.Any() == true
            || typed.StructuredContent != null
            || typed.IsError == true)
         {
            var finalResult = new CallToolResult()
            {
               Content = typed!.Content,
               StructuredContent = typed.StructuredContent,
               IsError = typed.IsError,
            };

            return JsonSerializer.Serialize(finalResult, JsonSerializerOptions.Web);
         }

         return JsonSerializer.Serialize(tip.Output, JsonSerializerOptions.Web);
      }
      catch (JsonException)
      {
         return JsonSerializer.Serialize(tip.Output, JsonSerializerOptions.Web);
      }
   }


   public static string? GetModel(this CreateMessageRequestParams messageRequestParams) =>
      messageRequestParams.ModelPreferences?.Hints?.FirstOrDefault()?.Name?.SplitModelId().Model;

   public static string? ToText(this SamplingMessage result) =>
         string.Join("\n\n", result.Content.OfType<TextContentBlock>().Select(a => a.Text));


   public static TextContentBlock ToTextContentBlock(this string text)
      => new()
      {
         Text = text
      };
}
