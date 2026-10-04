using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    private const string VideoOperationPrefix = "appnzv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = GetMediaOptions(request.ProviderOptions);
        var kind = ReadOptionString(payload, "generationType") ?? "generation";
        payload.Remove("generationType");
        var endpoint = kind.ToLowerInvariant() switch
        {
            "generation" or "generations" or "video" => "v1/videos/generations",
            "edit" or "edits" => "v1/videos/edits",
            "extension" or "extensions" => "v1/videos/extensions",
            _ => throw new NotSupportedException($"AppNZ video generationType '{kind}' is not supported.")
        };
        if (request.N is not null and not 1 || request.Fps is not null || request.Seed is not null
            || request.Resolution is not null || request.GenerateAudio is not null
            || request.FrameImages?.Any() == true)
            throw new NotSupportedException("AppNZ public video contracts do not verify generic n, fps, seed, resolution, generateAudio or frameImages mappings. Use raw AppNZ options for model-specific fields.");

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        var references = request.InputReferences?.ToArray() ?? [];
        if (endpoint == "v1/videos/generations")
        {
            if (request.Duration is { } duration) payload["duration"] = duration;
            if (request.AspectRatio is { } ratio) payload["aspect_ratio"] = ratio;
            if (request.Image is not null && references.Length > 0)
                throw new NotSupportedException("Supply image or inputReferences, not both.");
            if (request.Image is not null) payload["image_url"] = GetVideoInput(request.Image, false);
            if (references.Length > 0)
                payload["image_urls"] = references.Select(file => GetVideoInput(file, false)).ToArray();
        }
        else
        {
            if (request.Image is not null || request.AspectRatio is not null)
                throw new NotSupportedException("AppNZ video edit/extension contracts accept a source video, not image or aspectRatio.");
            if (endpoint == "v1/videos/edits" && request.Duration is not null)
                throw new NotSupportedException("Duration is verified for extensions, not edits.");
            if (request.Duration is { } duration) payload["duration"] = duration;
            if (references.Length > 1)
                throw new NotSupportedException("AppNZ video edit/extension accepts one source video.");
            if (references.Length == 1)
                payload["video"] = new { url = GetVideoInput(references[0], true) };
            if (!payload.TryGetValue("video", out var source)
                || ReadString(JsonSerializer.SerializeToElement(source, Json), "url") is not { } sourceUrl)
                throw new ArgumentException("Video edit/extension requires inputReferences with one video URL, or appnz.video.url.", nameof(request));
            ValidateMediaUrl(sourceUrl);
        }

        using var client = CreateClient();
        var raw = await SendJsonAsync(client, HttpMethod.Post, endpoint, payload, cancellationToken);
        var id = GetJobId(raw);
        if (GetMediaUrl(raw, "video") is null && id is null)
            throw new InvalidOperationException($"AppNZ video response has neither a media URL nor a verified job identifier: {raw.GetRawText()}");
        // Inline responses are carried in the token so polling remains stateless even for synchronous providers.
        var token = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = request.Model,
            id,
            result = GetMediaUrl(raw, "video") is not null ? (JsonElement?)raw : null
        }, Json);
        return new VideoOperationStartResult
        {
            Operation = VideoOperationPrefix + Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            ProviderMetadata = CreateMediaMetadata(raw),
            Response = CreateMediaResponse(request.Model)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!operation.StartsWith(VideoOperationPrefix, StringComparison.Ordinal) || operation.Length > 262144)
            throw new ArgumentException("Invalid AppNZ video operation token.", nameof(operation));
        JsonElement token;
        try
        {
            var encoded = operation[VideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            token = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid AppNZ video operation token.", nameof(operation), ex);
        }
        var model = ReadString(token, "model")
            ?? throw new ArgumentException("AppNZ video operation has no model.", nameof(operation));
        JsonElement raw;
        if (token.TryGetProperty("result", out var inline) && inline.ValueKind == JsonValueKind.Object)
            raw = inline.Clone();
        else
        {
            var id = ReadString(token, "id")
                ?? throw new ArgumentException("AppNZ video operation has no job identifier.", nameof(operation));
            using var client = CreateClient();
            raw = await SendJsonAsync(client, HttpMethod.Get, "v1/videos/" + Uri.EscapeDataString(id), null, cancellationToken);
        }
        var metadata = CreateMediaMetadata(raw);
        var response = CreateMediaResponse(model);
        var state = GetJobStatus(raw);
        if (state is "failed" or "expired")
            return new VideoOperationErrorResult
            {
                Error = GetMediaError(raw, $"AppNZ video job {state}."),
                ProviderMetadata = metadata,
                Response = response
            };
        var url = GetMediaUrl(raw, "video");
        if (url is not null)
        {
            var (bytes, mimeType) = await DownloadMediaAsync(url, "video", cancellationToken);
            return new VideoOperationCompletedResult
            {
                Videos = [new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(bytes),
                    MediaType = mimeType
                }],
                ProviderMetadata = metadata,
                Response = response
            };
        }
        if (state is "completed" or "succeeded")
            return new VideoOperationErrorResult
            {
                Error = "AppNZ video response has no downloadable video URL.",
                ProviderMetadata = metadata,
                Response = response
            };

        return new VideoOperationPendingResult
        {
            ProviderMetadata = metadata,
            Response = response
        };
    }

}
