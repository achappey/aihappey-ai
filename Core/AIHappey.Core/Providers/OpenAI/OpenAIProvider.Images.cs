using AIHappey.Core.AI;
using AIHappey.Common.Model;
using OpenAI.Images;

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

        if (imageRequest.N.HasValue && imageRequest.N.Value > 1)
        {
            var result = await responseClient.GenerateImagesAsync(imageRequest.Prompt, imageRequest.N.Value, new ImageGenerationOptions()
            {
                Size = imageRequest.Size.ToGeneratedImageSize(),
                OutputFileFormat = GeneratedImageFileFormat.Png,
            }, cancellationToken);

            results.AddRange(result.Value.Select(a => $"data:image/png;base64,{Convert.ToBase64String(a.ImageBytes)}"));
        }
        else
        {
            var result = await responseClient.GenerateImageAsync(imageRequest.Prompt, new ImageGenerationOptions()
            {
                Size = imageRequest.Size.ToGeneratedImageSize(),
                OutputFileFormat = GeneratedImageFileFormat.Png,
            }, cancellationToken);

            results.Add($"data:image/png;base64,{Convert.ToBase64String(result.Value.ImageBytes)}");
        }

        return new ImageResponse()
        {
            Images = results,
            Response = new ImageResponseData()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

}