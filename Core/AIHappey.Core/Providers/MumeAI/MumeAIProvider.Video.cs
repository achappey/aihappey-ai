using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MumeAI;

public partial class MumeAIProvider
{
    private const string MumeVideoOperationPrefix = "muv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var payload = MumePayload(GetMumeProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (request.Duration.HasValue) payload["duration"] = request.Duration.Value;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;
        if (request.Fps.HasValue) payload["fps"] = request.Fps.Value;
        if (request.GenerateAudio.HasValue) payload["generate_audio"] = request.GenerateAudio.Value;

        var frames = BuildMumeFrameImages(request);
        if (frames.Count > 0)
            payload["frame_images"] = frames;

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = JsonContent.Create(payload)
        };
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI video generation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = MumeString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Mume AI video generation returned no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeMumeVideoOperation(id, request.Model),
            Warnings = request.N is > 1 ? [new { type = "unsupported", feature = "n" }] : [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeMumeVideoOperation(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/videos/{Uri.EscapeDataString(operationData.Id)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI video status failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var status = MumeString(root, "status")?.Trim().ToLowerInvariant();

        if (status == "failed")
            return new VideoOperationErrorResult
            {
                Error = MumeString(root, "error") ?? $"Mume AI video job '{operationData.Id}' failed.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        if (status != "completed")
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = responseData };

        var url = MumeString(root, "url");
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult
            {
                Error = $"Mume AI video job '{operationData.Id}' completed without a URL.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        using var videoResponse = await _client.GetAsync(url, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mume AI video download failed ({(int)videoResponse.StatusCode}).");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(bytes),
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? GuessMumeVideoMediaType(url)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static List<Dictionary<string, object?>> BuildMumeFrameImages(VideoRequest request)
    {
        var frames = new List<Dictionary<string, object?>>();
        if (request.Image is not null)
            frames.Add(MumeFrame(request.Image, "first_frame"));
        if (request.FrameImages is not null)
            frames.AddRange(request.FrameImages.Select(frame => MumeFrame(frame.Image, frame.FrameType)));
        return frames;
    }

    private static Dictionary<string, object?> MumeFrame(VideoFile file, string frameType)
        => new()
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?> { ["url"] = NormalizeMumeVideoImage(file) },
            ["frame_type"] = frameType
        };

    private static string NormalizeMumeVideoImage(VideoFile file)
    {
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return file.Data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? "image/png" : file.MediaType);
    }

    private static string EncodeMumeVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new { id, model });
        return MumeVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (string Id, string Model) DecodeMumeVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(MumeVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Mume AI video operation token is invalid.", nameof(operation));
        try
        {
            var base64 = operation[MumeVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (base64.Length % 4 != 0)
                base64 = base64.PadRight(base64.Length + 4 - base64.Length % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            var id = MumeString(document.RootElement, "id");
            var model = MumeString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Mume AI video operation token is invalid.", nameof(operation));
            return (id, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Mume AI video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string GuessMumeVideoMediaType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";
}
