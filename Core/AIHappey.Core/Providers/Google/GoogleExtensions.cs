using Mscc.GenerativeAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public static partial class GoogleExtensions
{
    public static Part? ToImagePart(
       this ImageFile imageContentBlock) =>
       new()
       {
           InlineData = new()
           {
               MimeType = imageContentBlock.MediaType,
               Data = imageContentBlock.Data
           }
       };


    public static ImageAspectRatio? ToImageAspectRatio(this string? value) =>
      value switch
      {
          "1:1" => ImageAspectRatio.Ratio1x1,
          "9:16" => ImageAspectRatio.Ratio9x16,
          "16:9" => ImageAspectRatio.Ratio16x9,
          "4:3" => ImageAspectRatio.Ratio4x3,
          "3:4" => ImageAspectRatio.Ratio3x4,
          "2:3" => ImageAspectRatio.Ratio2x3,
          "3:2" => ImageAspectRatio.Ratio3x2,
          "21:9" => ImageAspectRatio.Ratio21x9,
          _ => null
      };

    public static ChatSession ToChatSession(
        this GoogleAI googleAI,
                GenerationConfig generationConfig,
                   string model,
                   string systemInstructions,
                   List<ContentResponse> history)
    {
        Content? systemInstruction = !string.IsNullOrEmpty(systemInstructions)
            ? new(systemInstructions, "model") : null;

        var generativeModel = googleAI.GenerativeModel(model,
            systemInstruction: systemInstruction
        );

        return generativeModel.StartChat(history, generationConfig);
    }
    
    public static string Identifier() => nameof(Google).ToLowerInvariant();

}
