using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Glio;

public partial class GlioProvider
{
    private const string GlioVideoOperationTokenPrefix = "glv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyGlioRootOptions(options);
        var parameters = GetGlioParams(payload);
        payload["model"] = request.Model;
        payload["action"] = "generate";
        parameters["prompt"] = request.Prompt;
        SetGlioValue(parameters, "resolution", request.Resolution);
        SetGlioValue(parameters, "aspect_ratio", request.AspectRatio);
        SetGlioValue(parameters, "seed", request.Seed);
        SetGlioValue(parameters, "duration", request.Duration);
        SetGlioValue(parameters, "fps", request.Fps);
        SetGlioValue(parameters, "n", request.N);

        if (request.Image is not null) parameters["image"] = ToGlioDataUrl(request.Image.Data, request.Image.MediaType);
        var references = request.InputReferences?.Where(reference => reference is not null).Select(reference => ToGlioDataUrl(reference.Data, reference.MediaType)).ToList() ?? [];
        if (references.Count > 0) parameters["input_references"] = references;
        var frames = request.FrameImages?.Where(frame => frame?.Image is not null).Select(frame => new Dictionary<string, object?>
        {
            ["frame_type"] = frame.FrameType,
            ["image"] = ToGlioDataUrl(frame.Image.Data, frame.Image.MediaType)
        }).ToList() ?? [];
        if (frames.Count > 0) parameters["frame_images"] = frames;

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/jobs")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, GlioJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var root = await ReadGlioJsonAsync(createResponse, "job creation", cancellationToken);
        var jobId = TryGetGlioString(root, "id") ?? throw new InvalidOperationException("Glio job creation returned no id.");
        return new VideoOperationStartResult
        {
            Operation = EncodeGlioVideoOperation(jobId, request.Model),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = createResponse.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var (jobId, model) = DecodeGlioVideoOperation(operation);
        ApplyAuthHeader();
        using var statusResponse = await _client.GetAsync($"v1/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        var root = await ReadGlioJsonAsync(statusResponse, "job status", cancellationToken);
        var status = TryGetGlioString(root, "status") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = statusResponse.GetHeaders(), ModelId = model.ToModelId(GetIdentifier()) };

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = GetGlioFailure(root), ProviderMetadata = metadata, Response = response };
        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var urls = ExtractGlioResultUrls(root);
        if (urls.Count == 0)
            return new VideoOperationErrorResult { Error = $"Glio job '{jobId}' completed without output URLs.", ProviderMetadata = metadata, Response = response };

        List<VideoOperationVideoData> videos = [];
        foreach (var url in urls)
        {
            var media = await DownloadGlioMediaAsync(url, GuessGlioVideoMediaType(url), cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(media.Bytes), MediaType = media.MediaType });
        }

        var deletion = await DeleteGlioJobAsync(jobId, cancellationToken);
        metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { status = root, delete = deletion });
        return new VideoOperationCompletedResult { Videos = videos, Warnings = [], ProviderMetadata = metadata, Response = response };
    }

    private static string GuessGlioVideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4"
        };
    }

    private static string EncodeGlioVideoOperation(string id, string model)
        => GlioVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id, ["model"] = model })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Id, string Model) DecodeGlioVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(GlioVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware Glio video operation token is required.", nameof(operation));
        try
        {
            var value = operation[GlioVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            var id = TryGetGlioString(document.RootElement, "id");
            var model = TryGetGlioString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("The Glio video operation token is invalid.", nameof(operation));
            return (id, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Glio video operation token is invalid.", nameof(operation), exception);
        }
    }
}
