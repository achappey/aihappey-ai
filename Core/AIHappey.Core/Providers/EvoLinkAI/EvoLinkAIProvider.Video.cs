using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EvoLinkAI;

public partial class EvoLinkAIProvider
{
    private const string EvoLinkAIVideoEndpoint = "v1/videos/generations";
    private const string EvoLinkAIVideoOperationTokenPrefix = "evv1_";

    private sealed record EvoLinkAIVideoOperationData(string TaskId, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var providerOptions = GetEvoLinkAIProviderOptions(request.ProviderOptions);
        var payload = CreateEvoLinkAIPassthroughPayload(providerOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null) payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["quality"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.N is not null) payload["n"] = request.N;
        if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio;

        var firstFrame = request.FrameImages?.FirstOrDefault(x => x.FrameType.Equals("first_frame", StringComparison.OrdinalIgnoreCase))?.Image
            ?? request.Image;
        var lastFrame = request.FrameImages?.FirstOrDefault(x => x.FrameType.Equals("last_frame", StringComparison.OrdinalIgnoreCase))?.Image;
        if (firstFrame is not null)
            payload["image_start"] = await ResolveEvoLinkAIInputUrlAsync(firstFrame.Data, firstFrame.MediaType, "first-frame.png", cancellationToken);
        if (lastFrame is not null)
            payload["image_end"] = await ResolveEvoLinkAIInputUrlAsync(lastFrame.Data, lastFrame.MediaType, "last-frame.png", cancellationToken);

        var references = new List<string>();
        foreach (var reference in request.InputReferences ?? [])
            references.Add(await ResolveEvoLinkAIInputUrlAsync(reference.Data, reference.MediaType, null, cancellationToken));
        if (references.Count > 0) payload["image_urls"] = references;

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, EvoLinkAIVideoEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, EvoLinkAISpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"EvoLinkAI video request failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var createRoot = document.RootElement.Clone();
        var createResult = NormalizeEvoLinkAITaskResult(createRoot, null);
        if (string.IsNullOrWhiteSpace(createResult.TaskId))
            throw new InvalidOperationException("EvoLinkAI video request returned no task id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeEvoLinkAIVideoOperation(createResult.TaskId, request.Model),
            Warnings = request.Fps is null ? [] : [new { type = "unsupported", feature = "fps" }],
            ProviderMetadata = CreateEvoLinkAIMetadata(
                EvoLinkAIVideoEndpoint, payload, createRoot, taskId: createResult.TaskId,
                status: createResult.Status, createHeaders: createResponse.GetHeaders()),
            Response = new HeaderResponseData
            {
                Timestamp = ResolveEvoLinkAITimestamp(createRoot, DateTime.UtcNow),
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeEvoLinkAIVideoOperation(operation);
        ApplyAuthHeader();
        var terminal = await PollEvoLinkAITaskAsync(operationData.TaskId, cancellationToken);
        var response = new HeaderResponseData
        {
            Timestamp = ResolveEvoLinkAITimestamp(terminal.Root, DateTime.UtcNow),
            Headers = terminal.Headers,
            // The submitted model is authoritative because EvoLink task polling
            // may omit the model or expose a routed upstream model.
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetEvoLinkAIProviderMetadata(new
        {
            endpoint = $"v1/tasks/{operationData.TaskId}",
            taskId = operationData.TaskId,
            status = terminal.Status,
            retrieve = terminal.Root,
            retrieveHeaders = terminal.Headers
        });

        if (!IsEvoLinkAITerminalStatus(terminal.Status))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        if (!IsEvoLinkAISuccessStatus(terminal.Status))
            return new VideoOperationErrorResult
            {
                Error = $"EvoLinkAI video generation failed with status '{terminal.Status}': {GetEvoLinkAITaskError(terminal.Root)}",
                ProviderMetadata = metadata,
                Response = response
            };

        var urls = GetEvoLinkAIResultUrls(terminal.Root, "video");
        if (urls.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"EvoLinkAI video task '{operationData.TaskId}' completed but returned no video URL.",
                ProviderMetadata = metadata,
                Response = response
            };

        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls)
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                videos.Add(new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = TryReadDataUrlMediaType(url) ?? "video/mp4",
                    Data = ExtractBase64Payload(url)
                });
                continue;
            }

            var media = await DownloadEvoLinkAIMediaAsync(url, GuessEvoLinkAIVideoMediaType(url) ?? "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", MediaType = media.MediaType, Data = Convert.ToBase64String(media.Bytes) });
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeEvoLinkAIVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new EvoLinkAIVideoOperationData(taskId, model), EvoLinkAISpeechJsonOptions);
        return EvoLinkAIVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static EvoLinkAIVideoOperationData DecodeEvoLinkAIVideoOperation(string operation)
    {
        if (!operation.StartsWith(EvoLinkAIVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The EvoLinkAI video operation token is invalid.", nameof(operation));

        var base64 = operation[EvoLinkAIVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 != 0) base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');
        try
        {
            var data = JsonSerializer.Deserialize<EvoLinkAIVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), EvoLinkAISpeechJsonOptions);
            return data is not null && !string.IsNullOrWhiteSpace(data.TaskId) && !string.IsNullOrWhiteSpace(data.Model)
                ? data
                : throw new ArgumentException("The EvoLinkAI video operation token is invalid.", nameof(operation));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The EvoLinkAI video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string? GuessEvoLinkAIVideoMediaType(string url)
        => Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mp4" => "video/mp4",
            _ => null
        };
}
