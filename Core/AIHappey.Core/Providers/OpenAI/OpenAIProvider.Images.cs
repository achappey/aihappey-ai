using OpenAI.Images;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        var responseClient = new ImageClient(
           imageRequest.Model,
           GetKey()
       );

        var now = DateTime.UtcNow;
        List<string> results = [];
        List<object> warnings = [];

        var imageSize = imageRequest.Size?.ToGeneratedImageSize()
            ?? imageRequest.AspectRatio?.ToGeneratedImageSizeFromAspectRatio();

        if (imageRequest.Files?.Any() == true)
        {
            var metadata = imageRequest.GetProviderMetadata<OpenAiImageEditProviderMetadata>(GetIdentifier());

            GeneratedImageQuality? quality = null;

            if (!string.IsNullOrEmpty(metadata?.Quality))
            {
                quality = new GeneratedImageQuality(metadata.Quality);
            }

            GeneratedImageBackground? background = null;

            if (!string.IsNullOrEmpty(metadata?.Background))
            {
                background = new GeneratedImageBackground(metadata.Background);
            }

            var provider = new FileExtensionContentTypeProvider();
            var file = imageRequest.Files.First();

            var bytes = Convert.FromBase64String(file.Data);
            using var stream = new MemoryStream(bytes);

            var result = await responseClient.GenerateImageEditAsync(stream,
                "input_file.png", imageRequest.Prompt, new ImageEditOptions()
                {
                    Size = imageSize,
                    Background = background,
                    Quality = quality
                }, cancellationToken);

            results.Add($"data:image/png;base64,{Convert.ToBase64String(result.Value.ImageBytes)}");

            if (imageRequest.Files.Count() > 1)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = $"Input File count {imageRequest.Files.Count()} not supported. Used single input image."
                });
            }
        }
        else
        {
            var metadata = imageRequest.GetProviderMetadata<OpenAiImageProviderMetadata>(GetIdentifier());
            GeneratedImageQuality? quality = null;

            if (!string.IsNullOrEmpty(metadata?.Quality))
            {
                quality = new GeneratedImageQuality(metadata.Quality);
            }

            GeneratedImageBackground? background = null;

            if (!string.IsNullOrEmpty(metadata?.Background))
            {
                background = new GeneratedImageBackground(metadata.Background);
            }

            GeneratedImageModerationLevel? moderation = null;
            if (!string.IsNullOrEmpty(metadata?.Moderation))
            {
                moderation = new GeneratedImageModerationLevel(metadata.Moderation);
            }

            if (imageRequest.N.HasValue && imageRequest.N.Value > 1)
            {
                var result = await responseClient.GenerateImagesAsync(imageRequest.Prompt, imageRequest.N.Value, new ImageGenerationOptions()
                {
                    Size = imageSize,
                    Quality = quality,
                    Background = background,
                    ModerationLevel = moderation,
                    OutputFileFormat = GeneratedImageFileFormat.Png,
                }, cancellationToken);

                results.AddRange(result.Value.Select(a => $"data:image/png;base64,{Convert.ToBase64String(a.ImageBytes)}"));
            }
            else
            {
                var result = await responseClient.GenerateImageAsync(imageRequest.Prompt, new ImageGenerationOptions()
                {
                    Size = imageSize,
                    Quality = quality,
                    Background = background,
                    ModerationLevel = moderation,
                    OutputFileFormat = GeneratedImageFileFormat.Png,
                }, cancellationToken);

                results.Add($"data:image/png;base64,{Convert.ToBase64String(result.Value.ImageBytes)}");
            }
        }


        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }


        if (imageRequest.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (!string.IsNullOrEmpty(imageRequest.Size) && imageSize is null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = $"Requested size {imageRequest.Size} not supported. Used default settings."
            });
        }

        return new ImageResponse()
        {
            Images = results,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }
}
