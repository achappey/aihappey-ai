using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        if (NormalizeAgentModel(request.Model) != "Nova") throw new NotSupportedException("MIMICXAI video generation requires Nova.");
        var result = await PostJsonAsync(AgentEndpoint, new { model = "Nova", prompt = request.Prompt, stream = false }, "video generation", cancellationToken);
        var warnings = new List<object>();
        if (request.Duration is not null || request.Resolution is not null || request.AspectRatio is not null || request.Fps is not null || request.Seed is not null || request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "video generation controls" });
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "image inputs" });
        return new VideoResponse
        {
            Videos = [new VideoResponseFile { Data = RequireBase64(result.Root, "video", "video_b64"), MediaType = "video/mp4" }],
            Warnings = warnings, ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }
}
