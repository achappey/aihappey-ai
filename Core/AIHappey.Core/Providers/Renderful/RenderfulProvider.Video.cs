using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
  
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        List<object> warnings = [];
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mediaInputs",
                details = "The documented Renderful text-to-video schema does not define generic image/reference/frame fields. Supply model-specific input fields through providerOptions.renderful."
            });
        }
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var payload = CreateRenderfulPayload(request.ProviderOptions, new Dictionary<string, object?>
        {
            ["type"] = "text-to-video",
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["duration"] = request.Duration,
            ["seed"] = request.Seed
        });

        var generation = await CreateGenerationAsync(payload, cancellationToken);
        List<VideoResponseFile> videos = [];
        foreach (var output in generation.Outputs)
        {
            var (bytes, mediaType) = await DownloadOutputAsync(output, "video/mp4", cancellationToken);
            videos.Add(new VideoResponseFile
            {
                Data = Convert.ToBase64String(bytes),
                MediaType = mediaType
            });
        }

        if (videos.Count == 0)
            throw new InvalidOperationException("Renderful video generation completed without output videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = CreateRenderfulMetadata(generation.Root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

}
