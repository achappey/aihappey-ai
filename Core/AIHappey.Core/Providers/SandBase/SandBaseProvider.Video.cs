using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
{
    private const string VideoTokenPrefix = "sbv1_";
    private sealed record SandBaseVideoOperation(string Id, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var payload = CreateRunPayload(request.ProviderOptions, request.Model);
        if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (request.Duration.HasValue) payload["duration"] = request.Duration.Value;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (request.Fps.HasValue) payload["fps"] = request.Fps.Value;
        if (request.GenerateAudio.HasValue) payload["generate_audio"] = request.GenerateAudio.Value;

        if (request.Image is not null)
        {
            var url = RequireMediaReference(request.Image.Data, "image");
            if (!payload.ContainsKey("image") && !payload.ContainsKey("image_url")) payload["image"] = url;
        }
        if (request.InputReferences is not null)
        {
            var urls = request.InputReferences.Select(file => RequireMediaReference(file.Data, "inputReferences")).ToArray();
            if (urls.Length > 0 && !payload.ContainsKey("images") && !payload.ContainsKey("videos"))
                throw new ArgumentException("SandBase inputReferences require model-specific images/videos fields in providerOptions.sandbase; their shape varies by model.");
        }
        if (request.FrameImages is not null)
        {
            var frames = request.FrameImages.ToArray();
            foreach (var frame in frames) RequireMediaReference(frame.Image.Data, "frameImages");
            if (frames.Length > 0 && !payload.ContainsKey("first_frame") && !payload.ContainsKey("last_frame"))
                throw new ArgumentException("SandBase frameImages require model-specific frame fields in providerOptions.sandbase.");
        }

        var run = await SubmitRunAsync(payload, cancellationToken);
        var id = RunString(run.Root, "id") ?? throw new InvalidOperationException("SandBase video run response did not include an id.");
        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(id, request.Model),
            ProviderMetadata = RunMetadata(run.Root),
            Response = new() { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Headers = run.Headers }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var token = DecodeVideoOperation(operation);
        var run = await PollRunAsync(token.Id, cancellationToken);
        var metadata = RunMetadata(run.Root);
        var response = new HeaderResponseData { ModelId = token.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Headers = run.Headers };
        switch (RunStatus(run.Root))
        {
            case "pending":
            case "running": return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };
            case "failed":
            case "timeout": return new VideoOperationErrorResult { Error = RunError(run.Root), ProviderMetadata = metadata, Response = response };
            case "completed": break;
            default: throw new InvalidOperationException("SandBase returned an unknown video run status.");
        }

        var videos = new List<VideoOperationVideoData>();
        foreach (var output in RunOutputs(run.Root))
        {
            var url = FindMedia(output, "video");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var (bytes, mime) = await ReadRunMediaAsync(url, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = mime });
        }
        if (videos.Count == 0)
            return new VideoOperationErrorResult { Error = "SandBase completed the video run without a usable video output.", ProviderMetadata = metadata, Response = response };
        return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = response };
    }

    private static string EncodeVideoOperation(string id, string model)
        => VideoTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SandBaseVideoOperation(id, model))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SandBaseVideoOperation DecodeVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(VideoTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid SandBase video operation token.", nameof(operation));
        try
        {
            var encoded = operation[VideoTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var token = JsonSerializer.Deserialize<SandBaseVideoOperation>(Convert.FromBase64String(encoded));
            if (string.IsNullOrWhiteSpace(token?.Id) || string.IsNullOrWhiteSpace(token.Model) || token.Id.Contains('/'))
                throw new ArgumentException("Invalid SandBase video operation token.", nameof(operation));
            return token;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid SandBase video operation token.", nameof(operation), error);
        }
    }
}
