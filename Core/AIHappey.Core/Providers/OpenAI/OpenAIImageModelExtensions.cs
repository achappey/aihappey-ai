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
}