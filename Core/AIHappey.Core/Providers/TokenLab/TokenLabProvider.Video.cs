using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TokenLab;

public partial class TokenLabProvider
{
    private const string TokenLabVideoOperationPrefix = "tlv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = CreateTokenLabPayload(GetTokenLabProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.Duration is not null) payload["duration"] = request.Duration;
        if (request.Fps is not null) payload["fps"] = request.Fps;
        if (request.N is not null) payload["n"] = request.N;
        if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio;
        if (request.Image is not null) payload["image"] = ToVideoDataUrl(request.Image);
        if (request.InputReferences?.Any() == true)
            payload["input_references"] = new JsonArray(request.InputReferences.Select(file => (JsonNode?)ToVideoDataUrl(file)).ToArray());
        if (request.FrameImages?.Any() == true)
            payload["frame_images"] = new JsonArray(request.FrameImages.Select(frame => (JsonNode?)new JsonObject
            {
                ["frame_type"] = frame.FrameType,
                ["image"] = ToVideoDataUrl(frame.Image)
            }).ToArray());

        var create = await SendTokenLabJsonAsync(HttpMethod.Post, "v1/videos/generations", ToJsonContent(payload), "video generation", cancellationToken);
        var taskId = FindTokenLabString(create.Root, "task_id", "taskId", "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException($"TokenLab video generation returned no task ID: {create.Root.GetRawText()}");
        var pollUrl = FindTokenLabString(create.Root, "poll_url", "pollUrl") ?? $"v1/tasks/{Uri.EscapeDataString(taskId)}";

        return new VideoOperationStartResult
        {
            Operation = EncodeTokenLabVideoOperation(taskId, request.Model, pollUrl),
            ProviderMetadata = CreateTokenLabMetadata(new { taskId, pollUrl, create = create.Root }),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = create.Headers
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));
        var data = DecodeTokenLabVideoOperation(operation);
        var poll = await SendTokenLabJsonAsync(HttpMethod.Get, data.PollUrl, null, "video generation poll", cancellationToken);
        var status = FindTokenLabString(poll.Root, "status", "state")?.ToLowerInvariant();
        var metadata = CreateTokenLabMetadata(new { taskId = data.TaskId, pollUrl = data.PollUrl, status, response = poll.Root });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = data.Model.ToModelId(GetIdentifier()),
            Headers = poll.Headers
        };

        if (status is "failed" or "failure" or "error" or "cancelled" or "canceled")
            return new VideoOperationErrorResult
            {
                Error = FindTokenLabString(poll.Root, "error", "message", "detail") ?? $"TokenLab video task '{data.TaskId}' failed.",
                ProviderMetadata = metadata,
                Response = response
            };

        if (status is not ("completed" or "complete" or "succeeded" or "success" or "done") && !HasTokenLabMediaResult(poll.Root))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videos = new List<VideoOperationVideoData>();
        foreach (var value in GetTokenLabMediaValues(poll.Root, "video"))
        {
            var media = await ResolveTokenLabMediaAsync(value, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(media.Bytes), MediaType = media.MimeType });
        }
        if (videos.Count == 0)
            return new VideoOperationErrorResult { Error = $"TokenLab video task '{data.TaskId}' completed without a video.", ProviderMetadata = metadata, Response = response };

        return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = response };
    }

    private static string ToVideoDataUrl(VideoFile file) => $"data:{file.MediaType};base64,{file.Data}";

    private static string EncodeTokenLabVideoOperation(string taskId, string model, string pollUrl)
    {
        var json = JsonSerializer.Serialize(new TokenLabVideoOperation(taskId, model, pollUrl), TokenLabJson);
        return TokenLabVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static TokenLabVideoOperation DecodeTokenLabVideoOperation(string operation)
    {
        if (!operation.StartsWith(TokenLabVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The TokenLab video operation token is invalid.", nameof(operation));
        try
        {
            var encoded = operation[TokenLabVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<TokenLabVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), TokenLabJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model) || string.IsNullOrWhiteSpace(data.PollUrl))
                throw new ArgumentException("The TokenLab video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The TokenLab video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record TokenLabVideoOperation(string TaskId, string Model, string PollUrl);
}
