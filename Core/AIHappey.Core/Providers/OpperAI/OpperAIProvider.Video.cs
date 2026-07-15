using System.Net.Mime;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private async Task<VideoResponse> OpperAIVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerOptions = GetOpperAIProviderOptions(request.ProviderOptions);
        var payload = BuildOpperAIVideoPayload(request, providerOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v3/videos")
        {
            Content = CreateOpperAIJsonContent(payload)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"OpperAI video generation failed ({(int)createResponse.StatusCode})."
                : $"OpperAI video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = System.Text.Json.JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var taskId = TryGetOpperAIString(createRoot, "id")
            ?? throw new InvalidOperationException("OpperAI video generation returned no id.");
        var statusUrl = TryGetOpperAIString(createRoot, "status_url", "statusUrl")
            ?? throw new InvalidOperationException($"OpperAI video generation returned no status_url (id={taskId}).");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: token => PollOpperAIVideoStatusAsync(statusUrl, token),
            isTerminal: result => IsOpperAITerminalStatus(result.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(20),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!IsOpperAISuccessStatus(completed.Status))
            throw new InvalidOperationException($"OpperAI video generation failed with status '{completed.Status}' (id={taskId}). Response: {completed.Root}");

        var videoUrl = ExtractOpperAIVideoUrl(completed.Root)
            ?? throw new InvalidOperationException($"OpperAI video task completed but returned no downloadable URL (id={taskId}).");
        var downloaded = await DownloadOpperAIMediaAsync(videoUrl, "video/mp4", cancellationToken);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    Data = Convert.ToBase64String(downloaded.Bytes),
                    MediaType = downloaded.MediaType
                }
            ],
            Warnings = [],
            ProviderMetadata = CreateOpperAIMediaMetadata(new
            {
                endpoint = "v3/videos",
                statusUrl,
                id = taskId,
                payload,
                create = createRoot,
                status = completed.Root,
                videoUrl
            }),
            Response = new()
            {
                Timestamp = now,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildOpperAIVideoPayload(
        VideoRequest request,
        Dictionary<string, object?> providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["seconds"] = request.Duration
        };

        if (request.Image is not null)
            payload["image"] = NormalizeOpperAIInputFile(request.Image.Data, request.Image.MediaType);

        if (request.InputReferences?.Any() == true)
        {
            payload["reference_images"] = request.InputReferences
                .Select(reference => NormalizeOpperAIInputFile(reference.Data, reference.MediaType))
                .ToArray();
        }

        if (request.FrameImages?.Any() == true)
        {
            var lastImage = request.FrameImages.FirstOrDefault(frame =>
                string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(frame.FrameType, "last", StringComparison.OrdinalIgnoreCase));

            if (lastImage is not null)
                payload["last_image"] = NormalizeOpperAIInputFile(lastImage.Image.Data, lastImage.Image.MediaType);
        }

        AddOpperAIParameters(payload, providerOptions);
        return payload;
    }

    private async Task<OpperAIVideoStatus> PollOpperAIVideoStatusAsync(
        string statusUrl,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(statusUrl, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI video status failed ({(int)response.StatusCode})."
                : $"OpperAI video status failed ({(int)response.StatusCode}): {raw}");

        using var document = System.Text.Json.JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = TryGetOpperAIString(root, "status", "state") ?? "unknown";
        return new OpperAIVideoStatus(status, root);
    }
}
