using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SudoRouter;

public partial class SudoRouterProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "SudoRouter's documented video endpoint returns a single task." });
        if (request.Fps.HasValue)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "inputReferences/frameImages", details = "Use SudoRouter provider options for provider-specific video controls." });

        var payload = GetSudoRouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Duration.HasValue)
            payload["duration"] = request.Duration.Value.ToString();
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (request.Image is not null)
            payload["image"] = NormalizeSudoRouterBase64(request.Image.Data);

        var creation = await SendSudoRouterJsonAsync(HttpMethod.Post, "v1/video/generations", payload, cancellationToken);
        var taskId = GetSudoRouterTaskId(creation.Root)
            ?? throw new InvalidOperationException("SudoRouter video generation returned no task_id.");

        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: token => SendSudoRouterJsonAsync(HttpMethod.Get, $"v1/video/generations/{Uri.EscapeDataString(taskId)}", null, token),
            isTerminal: status => IsSudoRouterVideoTerminal(status.Root),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = GetSudoRouterVideoStatus(terminal.Root);
        if (IsSudoRouterVideoFailure(status))
            throw new InvalidOperationException($"SudoRouter video generation failed with status '{status}' (task_id={taskId}): {terminal.Root.GetRawText()}");

        var urls = ExtractSudoRouterVideoUrls(terminal.Root);
        if (urls.Count == 0)
            throw new InvalidOperationException($"SudoRouter video task '{taskId}' completed without a video URL.");

        var videos = new List<VideoResponseFile>();
        foreach (var url in urls)
        {
            var binary = await DownloadSudoRouterMediaAsync(url, GuessSudoRouterVideoMediaType(url), cancellationToken);
            videos.Add(new VideoResponseFile
            {
                Data = Convert.ToBase64String(binary.Bytes),
                MediaType = binary.MediaType
            });
        }

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(JsonSerializer.SerializeToElement(new
            {
                create = creation.Root,
                status = terminal.Root
            })),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = terminal.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string? GetSudoRouterTaskId(JsonElement root)
        => root.TryGetProperty("task_id", out var taskId) && taskId.ValueKind == JsonValueKind.String
            ? taskId.GetString()
            : null;

    private static string GetSudoRouterVideoStatus(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String)
        {
            return status.GetString() ?? "UNKNOWN";
        }

        return "UNKNOWN";
    }

    private static bool IsSudoRouterVideoTerminal(JsonElement root)
    {
        var status = GetSudoRouterVideoStatus(root);
        return status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILURE", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSudoRouterVideoFailure(string status)
        => status.Equals("FAILURE", StringComparison.OrdinalIgnoreCase)
            || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);

    private static List<string> ExtractSudoRouterVideoUrls(JsonElement root)
    {
        var urls = new List<string>();
        CollectSudoRouterVideoUrls(root, urls);
        return urls.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void CollectSudoRouterVideoUrls(JsonElement element, List<string> urls)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("url") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var url = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        urls.Add(url);
                }

                CollectSudoRouterVideoUrls(property.Value, urls);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectSudoRouterVideoUrls(item, urls);
        }
    }

    private static string GuessSudoRouterVideoMediaType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase)
            ? "video/webm"
            : url.Contains(".mov", StringComparison.OrdinalIgnoreCase)
                ? "video/quicktime"
                : "video/mp4";

}
