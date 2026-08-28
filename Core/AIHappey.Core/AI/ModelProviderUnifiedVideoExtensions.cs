using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

/// <summary>
/// Adapts asynchronous provider video operations to the unified conversation
/// contract as a synthetic, provider-executed tool call.
/// </summary>
public static class ModelProviderUnifiedVideoExtensions
{
    private const string ToolName = "generate_video";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    public static async Task<bool> IsVideoModelAsync(
        this IModelProvider modelProvider,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var model = await modelProvider.GetModel(modelId, cancellationToken);
        return string.Equals(model.Type, "video", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AIResponse> ExecuteUnifiedVideoAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var videoRequest = CreateVideoRequest(request, providerId);
        var toolCallId = CreateToolCallId(request);
        var started = await modelProvider.StartVideoOperation(videoRequest, cancellationToken);
        var terminal = await PollUntilTerminalAsync(
            modelProvider,
            started.Operation,
            ValidatePollInterval(pollInterval),
            cancellationToken);

        var output = CreateCallToolResult(terminal, started.Operation);
        var failed = terminal is VideoOperationErrorResult;

        return new AIResponse
        {
            ProviderId = providerId,
            Model = request.Model,
            Status = failed ? "failed" : "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-call",
                                ToolCallId = toolCallId,
                                ToolName = ToolName,
                                Title = "Generate video",
                                Input = CreateToolInput(videoRequest),
                                State = failed ? "output-error" : "output-available",
                                Output = output,
                                ProviderExecuted = true,
                                Metadata = CreateToolMetadata(providerId, terminal.ProviderMetadata)
                            }
                        ]
                    }
                ]
            }
        };
    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedVideoAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var videoRequest = CreateVideoRequest(request, providerId);
        var toolCallId = CreateToolCallId(request);
        var interval = ValidatePollInterval(pollInterval);
        var started = await modelProvider.StartVideoOperation(videoRequest, cancellationToken);
        var startedMetadata = ToProviderMetadata(providerId, started.ProviderMetadata);

        yield return CreateEvent(providerId, "tool-input-available", toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = ToolName,
                Title = "Generate video",
                Input = CreateToolInput(videoRequest),
                ProviderExecuted = true,
                ProviderMetadata = startedMetadata
            });

        yield return CreateEvent(providerId, "tool-output-available", toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ToolName,
                Output = CreateProgressCallToolResult("submitted", started.Operation, started.Warnings),
                ProviderExecuted = true,
                Preliminary = true,
                ProviderMetadata = startedMetadata
            });

        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            var status = await modelProvider.GetVideoOperationStatus(started.Operation, cancellationToken);
            var providerMetadata = ToProviderMetadata(providerId, status.ProviderMetadata);

            if (status is VideoOperationPendingResult pending)
            {
                yield return CreateEvent(providerId, "tool-output-available", toolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = ToolName,
                        Output = CreateProgressCallToolResult("pending", started.Operation, pending.Warnings),
                        ProviderExecuted = true,
                        Preliminary = true,
                        ProviderMetadata = providerMetadata
                    });
                continue;
            }

            if (status is VideoOperationErrorResult error)
            {
                yield return CreateEvent(providerId, "tool-output-error", toolCallId,
                    new AIToolOutputErrorEventData
                    {
                        ToolCallId = toolCallId,
                        ErrorText = error.Error,
                        ProviderExecuted = true,
                        ProviderMetadata = providerMetadata
                    });

                yield return CreateFinishEvent(providerId, request.Model, toolCallId, "error");
                yield break;
            }

            if (status is not VideoOperationCompletedResult completed)
                throw new InvalidOperationException($"Unknown video operation status '{status.GetType().Name}'.");

            yield return CreateEvent(providerId, "tool-output-available", toolCallId,
                new AIToolOutputAvailableEventData
                {
                    ToolName = ToolName,
                    Output = CreateCallToolResult(completed, started.Operation),
                    ProviderExecuted = true,
                    Preliminary = false,
                    ProviderMetadata = providerMetadata
                });

            yield return CreateFinishEvent(providerId, request.Model, toolCallId, "tool-calls");
            yield break;
        }
    }

    private static VideoRequest CreateVideoRequest(AIRequest request, string providerId)
    {
        var model = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var lastUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var prompt = string.Join("\n", lastUserMessage?.Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Video conversation requests require a text prompt in the last user message.", nameof(request));

        var attachments = lastUserMessage?.Content?
            .OfType<AIFileContentPart>()
            .Where(file => IsImage(file.MediaType) || IsVideo(file.MediaType))
            .Select(ToVideoFile)
            .ToList() ?? [];
        var images = attachments.Where(file => IsImage(file.MediaType)).ToList();
        var references = attachments.Skip(images.Count > 0 ? 1 : 0).ToList();

        return new VideoRequest
        {
            Model = GetProviderModelId(model, providerId),
            Prompt = prompt,
            Image = images.FirstOrDefault(),
            InputReferences = references.Count == 0 ? null : references,
            ProviderOptions = ToProviderOptions(request.Metadata)
        };
    }

    private static VideoFile ToVideoFile(AIFileContentPart file)
    {
        var data = file.Data?.ToString();
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException("Video generation attachments must contain data or a URL.");

        return new VideoFile
        {
            Type = "file",
            MediaType = file.MediaType ?? "application/octet-stream",
            Data = data
        };
    }

    private static async Task<VideoOperationStatusResult> PollUntilTerminalAsync(
        IModelProvider modelProvider,
        string operation,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            var status = await modelProvider.GetVideoOperationStatus(operation, cancellationToken);
            if (status is not VideoOperationPendingResult)
                return status;
        }
    }

    private static CallToolResult CreateCallToolResult(VideoOperationStatusResult status, string operation)
    {
        if (status is VideoOperationErrorResult error)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error.Error }],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    status = "error",
                    operation,
                    error = error.Error
                }, JsonSerializerOptions.Web)
            };
        }

        var completed = status as VideoOperationCompletedResult
            ?? throw new InvalidOperationException("A completed video operation result was expected.");
        var videos = completed.Videos.ToList();
        var content = videos.Select((video, index) => new EmbeddedResourceBlock
        {
            Resource = new BlobResourceContents
            {
                Uri = $"video://generated/{Uri.EscapeDataString(operation)}/{index}",
                MimeType = video.MediaType,
                // MCP's wire contract requires resource.blob to be base64 text.
                // Assigning byte[] here can be surfaced by intermediate mappers as
                // decoded binary characters instead of the protocol representation.
                Blob = Encoding.UTF8.GetBytes(Convert.ToBase64String(GetVideoBytes(video.Data)))
            }
        }).Cast<ContentBlock>().ToList();

        return new CallToolResult
        {
            IsError = false,
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                status = "completed",
                operation,
                videos = videos.Select((video, index) => new
                {
                    index,
                    type = "embedded-resource",
                    mediaType = video.MediaType,
                    uri = $"video://generated/{Uri.EscapeDataString(operation)}/{index}"
                }),
                warnings = completed.Warnings
            }, JsonSerializerOptions.Web)
        };
    }

    private static CallToolResult CreateProgressCallToolResult(string status, string operation, IEnumerable<object>? warnings)
        => new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = $"Video generation is {status}." }],
            StructuredContent = JsonSerializer.SerializeToElement(new { status, operation, warnings }, JsonSerializerOptions.Web)
        };

    private static object CreateToolInput(VideoRequest request)
        => new
        {
            prompt = request.Prompt,
            model = request.Model,
            attachmentCount = (request.Image is null ? 0 : 1) + (request.InputReferences?.Count() ?? 0)
        };

    private static byte[] GetVideoBytes(object? data)
    {
        if (data is byte[] bytes)
            return bytes;

        var value = data?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("A completed video contained no binary data.");

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new InvalidOperationException("A completed video data URL was not base64 encoded.");
            value = value[(marker + ";base64,".Length)..];
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("A completed video did not contain valid base64 data.", ex);
        }
    }

    private static Dictionary<string, JsonElement>? ToProviderOptions(Dictionary<string, object?>? metadata)
        => metadata?.Where(entry => entry.Value is not null).ToDictionary(
            entry => entry.Key,
            entry => entry.Value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(entry.Value, JsonSerializerOptions.Web));

    private static Dictionary<string, object?> CreateToolMetadata(
        string providerId,
        Dictionary<string, JsonElement>? metadata)
        => new() { [providerId] = metadata?.ToDictionary(entry => entry.Key, entry => (object)entry.Value.Clone()) ?? [] };

    private static Dictionary<string, Dictionary<string, object>> ToProviderMetadata(
        string providerId,
        Dictionary<string, JsonElement>? metadata)
        => new()
        {
            [providerId] = metadata?.ToDictionary(entry => entry.Key, entry => (object)entry.Value.Clone()) ?? []
        };

    private static AIStreamEvent CreateFinishEvent(string providerId, string? model, string id, string reason)
        => CreateEvent(providerId, "finish", id, new AIFinishEventData
        {
            FinishReason = reason,
            Model = model
        });

    private static AIStreamEvent CreateEvent(string providerId, string type, string id, object data)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data
            }
        };

    private static string CreateToolCallId(AIRequest request)
        => $"video_{request.Id ?? Guid.NewGuid().ToString("N")}";

    private static TimeSpan ValidatePollInterval(TimeSpan? interval)
    {
        var value = interval ?? DefaultPollInterval;
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "The video poll interval must be greater than zero.");
        return value;
    }

    private static bool IsImage(string? mediaType)
        => mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsVideo(string? mediaType)
        => mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetProviderModelId(string model, string providerId)
    {
        var prefix = providerId + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }
}
