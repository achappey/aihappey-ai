using OpenAI.Images;

namespace AIHappey.Core.Providers.OpenAI;

public static class OpenAIImageModelExtensions
{

    public static GeneratedImageSize? ToGeneratedImageSize(this string? size) =>
       size?.Trim().ToLowerInvariant() switch
       {
           "256x256" => GeneratedImageSize.W256xH256,
           "512x512" => GeneratedImageSize.W512xH512,
           "1024x1024" => GeneratedImageSize.W1024xH1024,
           "1024x1536" => GeneratedImageSize.W1024xH1536,
           "1536x1024" => GeneratedImageSize.W1536xH1024,
           "1024x1792" => GeneratedImageSize.W1024xH1792,
           "1792x1024" => GeneratedImageSize.W1792xH1024,
           _ => null
       };

    public static GeneratedImageSize? ToGeneratedImageSizeFromAspectRatio(this string? aspectRatio) =>
    aspectRatio?.Trim().ToLowerInvariant() switch
    {
        // Square
        "1:1"  => GeneratedImageSize.W1024xH1024,

        // Portrait
        "2:3" or "3:4" or "9:16" =>
            GeneratedImageSize.W1024xH1536,

        // Landscape
        "3:2" or "4:3" or "16:9" =>
            GeneratedImageSize.W1536xH1024,

        _ => null
    };
}