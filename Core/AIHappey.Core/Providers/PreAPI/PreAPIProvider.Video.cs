using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
{
     public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = $"PreAPI currently returns a single primary generation per request. Requested n={request.N}." });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var input = BuildVideoInput(request);
        using var doc = await GenerateAsync(request.Model, input, cancellationToken);

        var data = GetResponseData(doc.RootElement);
        var output = GetOutput(data);
        var videoUrl = TryGetNestedString(output, "video", "url") ?? TryGetString(data, "output_url");
        var contentType = TryGetNestedString(output, "video", "content_type");

        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("PreAPI video generation returned no video URL.");

        var download = await DownloadMediaAsync(videoUrl, contentType, cancellationToken);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    Data = download.Base64,
                    MediaType = download.MediaType
                }
            ],
            Warnings = warnings,
            ProviderMetadata = CreateProviderMetadata(doc.RootElement),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }
}
