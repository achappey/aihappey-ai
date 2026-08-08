using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var inputs = new List<Dictionary<string, string>>();
        if (request.Image is not null) inputs.Add(ToApiAirforceVideoInput(request.Image));
        inputs.AddRange((request.InputReferences ?? []).Select(ToApiAirforceVideoInput));
        inputs.AddRange((request.FrameImages ?? []).Select(frame => ToApiAirforceVideoInput(frame.Image)));

        var mode = inputs.Count == 0 ? "text" : request.InputReferences?.Any() == true ? "reference" : "image";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelId(request.Model),
            ["prompt"] = request.Prompt,
        };

        if (mode is not null)
            payload["mode"] = mode;

        if (request.Duration is not null)
            payload["duration_seconds"] = request.Duration;

        if (request.AspectRatio is not null)
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Resolution is not null)
            payload["quality"] = request.Resolution;

        if (inputs.Count > 0)
            payload["input_images"] = inputs;

        if (request.GenerateAudio is not null)
            payload["sound"] = request.GenerateAudio;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "prompt", "mode", "duration_seconds", "aspect_ratio", "quality", "input_images"
        };
        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ApiAirforceMediaJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ApiAirforce video generation failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = TryGetString(root, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("ApiAirforce video generation returned no task_id.");

        var warnings = new List<object>();
        if (request.Fps is not null) AddUnsupportedWarning(warnings, "fps");
        if (request.N is > 1) AddUnsupportedWarning(warnings, "n");
        return new VideoOperationStartResult
        {
            Operation = taskId,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders()
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/video/tasks/{Uri.EscapeDataString(operation)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ApiAirforce video task failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = TryGetString(root, "status")?.ToLowerInvariant();
        var model = TryGetString(root, "model");
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);

        if (status is "failed" or "expired")
            return new VideoOperationErrorResult { Error = TryGetString(root, "error") ?? status, ProviderMetadata = metadata, Response = responseData };
        if (status != "completed")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };

        var resultUrl = TryGetString(root, "result_url");
        if (string.IsNullOrWhiteSpace(resultUrl))
            return new VideoOperationErrorResult { Error = $"ApiAirforce video task '{operation}' completed without result_url.", ProviderMetadata = metadata, Response = responseData };

        var downloaded = await TryFetchAsBase64Async(resultUrl, cancellationToken);
        if (downloaded is null)
            return new VideoOperationErrorResult { Error = $"ApiAirforce video result '{operation}' could not be downloaded.", ProviderMetadata = metadata, Response = responseData };

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", Data = downloaded.Value.Base64, MediaType = downloaded.Value.MediaType }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private Task<VideoResponse> VideoRequestApiAirforce(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ApiAirforce video generation is asynchronous. Use StartVideoOperation and GetVideoOperationStatus.");

    private static Dictionary<string, string> ToApiAirforceVideoInput(VideoFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Video input image data is required.", nameof(image));
        if (image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string> { ["url"] = image.Data };
        return new Dictionary<string, string> { ["b64_json"] = StripDataUrl(ToDataUrl(image)) };
    }
}
