using System.Net.Mime;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using Mscc.GenerativeAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        GoogleAI googleAI = GetClient();
        var now = DateTime.UtcNow;
        List<string> images = [];
        List<object> warnings = [];
        var imageClient = googleAI.ImageGenerationModel(imageRequest.Model);
        var aspectRatio = imageRequest.AspectRatio.ToImageAspectRatio();

        if (imageRequest.Model.Contains("imagen"))
        {
            var item = await imageClient.GenerateImages(new(imageRequest.Prompt, imageRequest.N)
            {
                Parameters = new()
                {
                    SampleCount = imageRequest.N,
                    AspectRatio = aspectRatio,
                    PersonGeneration = PersonGeneration.AllowAdult,
                    OutputOptions = new()
                    {
                        MimeType = MediaTypeNames.Image.Png
                    }
                }
            }, new RequestOptions(), cancellationToken: cancellationToken);

            foreach (var imageItem in item.Predictions)
            {
                if (imageItem.BytesBase64Encoded is not null)
                    images.Add(imageItem.BytesBase64Encoded.ToDataUrl("image/png"));
            }

            return new()
            {
                Images = images,
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = now,
                    ModelId = imageRequest.Model
                }
            };
        }
        else
        {
            List<ContentResponse> inputItems = [new(imageRequest.Prompt)
            {
                Role = "user",
                Parts = [.. imageRequest.Files?.Select(a => a.ToImagePart()).OfType<Part>() ?? []]
            }];

            ChatSession chat = googleAI.ToChatSession(new()
            {
                ResponseModalities = [ResponseModality.Text]
            }, imageRequest.Model,
                string.Empty,
                inputItems);

            List<Part> parts = [new Part() { Text =
             imageRequest.Prompt }];

            var response = await chat.SendMessage(parts,
                tools: null,
                cancellationToken: cancellationToken);

            List<string> imagesItems = response.Candidates?.FirstOrDefault()?.Content?
                .Parts.Where(a => a.InlineData != null)?
                .Select(a => a.InlineData)
                .Where(a => a?.MimeType.StartsWith("image/") == true)
                .Select(a => a?.Data!.ToDataUrl(a?.MimeType!))
                .OfType<string>()
                .ToList() ?? [];

            return new()
            {
                Images = imagesItems,
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = now,
                    ModelId = response.ModelVersion!
                },
                Usage = new()
                {
                    TotalTokens = response.UsageMetadata?.TotalTokenCount,
                    OutputTokens = response.UsageMetadata?.CandidatesTokenCount,
                    InputTokens = (response.UsageMetadata?.ToolUsePromptTokenCount ?? 0)
                                + (response.UsageMetadata?.PromptTokenCount ?? 0)
                }
            };


        }
    }
}
