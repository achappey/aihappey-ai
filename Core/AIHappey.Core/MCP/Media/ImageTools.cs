using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Media;

[McpServerToolType]
public class ImageTools
{
    [Description("Generate one or more images using the unified image endpoint.")]
    [McpServerTool(
        Title = "Generate image",
        Name = "ai_images_generate",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_ImageGenerate(
        [Description("AI model identifier")] string model,
        [Description("Prompt to create the image")] string prompt,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Number of images to create")] int n = 1,
        [Description("Size of the images to generate. Must have the format `{width}x{height}`")] string? size = null,
        [Description("Aspect ratio of the images to generate. Must have the format `{width}x{height}`")] string? aspectRatio = null,
        [Description("Seed for the image generation")] int? seed = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var request = new ImageRequest()
            {
                Model = model,
                Prompt = prompt,
                Size = size,
                Seed = seed,
                AspectRatio = aspectRatio,
                N = n,
            };

            if (string.IsNullOrWhiteSpace(request.Model))
                throw new ArgumentException("'model' is required.");
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("'prompt' is required.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

            var provider = await resolver.Resolve(request.Model, ct);
            request.Model = request.Model.SplitModelId().Model;

            var result = await provider.ImageRequest(request, ct);
            var images = result.Images?.ToList() ?? [];
            if (images.Count == 0)
                throw new InvalidOperationException("Provider returned no images.");

            List<ContentBlock> blocks = [];

            foreach (var img in images)
            {
                if (MediaContentHelpers.TryParseDataUrl(img, out var mimeType, out var base64))
                {
                    blocks.Add(new ImageContentBlock { MimeType = mimeType, Data = base64 });
                    continue;
                }

                throw new InvalidOperationException("Unsupported image output format. Expected data-url or http(s) URL.");
            }

            var structured = new JsonObject
            {
                ["modelId"] = result.Response?.ModelId,
                ["timestamp"] = result.Response?.Timestamp,
                ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, JsonSerializerOptions.Web),
                ["providerMetadata"] = JsonSerializer.SerializeToNode(result.ProviderMetadata, JsonSerializerOptions.Web)
            };

            return new CallToolResult
            {
                Content = [.. blocks],
                StructuredContent = structured
            };
        });
}

