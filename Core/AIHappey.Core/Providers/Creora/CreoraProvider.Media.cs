using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Creora;

public sealed partial class CreoraProvider
{
    private sealed record NativeResponse(JsonElement Root, Dictionary<string, string> Headers);

    private async Task<NativeResponse> SendJsonAsync(
        HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await _client.SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<NativeResponse> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, raw);
        using var document = JsonDocument.Parse(raw);
        return new(document.RootElement.Clone(), response.GetHeaders());
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Creora API error ({(int)response.StatusCode}): {body}",
                null, response.StatusCode);
    }

    private HeaderResponseData ResponseInfo(NativeResponse response, string model)
        => new()
        {
            ModelId = NativeModel(model).ToModelId(GetIdentifier()),
            Timestamp = (response.Root.TryGetProperty("created", out var created)
                || response.Root.TryGetProperty("created_at", out created))
                && created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow,
            Headers = response.Headers
        };

    private async Task<NativeResponse> GenerateImageAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        payload.Remove("stream");
        var response = await SendJsonAsync(HttpMethod.Post, "images/generations", payload, ct);
        var images = response.Root.GetProperty("data").EnumerateArray().ToArray();
        if (images.Length == 0 || images.Any(image =>
            !image.TryGetProperty("b64_json", out var data) || string.IsNullOrWhiteSpace(data.GetString())))
            throw new InvalidOperationException($"Creora returned no image data: {response.Root.GetRawText()}");
        return response;
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Creora does not document image editing or reference images.");
        var payload = CopyOptions(request.ProviderOptions);
        payload["model"] = NativeModel(request.Model);
        payload["prompt"] = request.Prompt;
        if (request.N is not null) payload["n"] = request.N;
        if (request.Size is not null) payload["size"] = request.Size;
        var response = await GenerateImageAsync(payload, cancellationToken);
        var images = response.Root.Deserialize<OpenAIImagesResponse>()!;
        images.OutputFormat ??= "webp";
        var warnings = new List<object>();
        if (request.AspectRatio is not null) warnings.Add(Unsupported("aspectRatio"));
        if (request.Seed is not null) warnings.Add(Unsupported("seed"));
        return new()
        {
            Images = images.Data!.Select(image => $"data:image/{images.OutputFormat};base64,{image.B64Json}"),
            Usage = images.Usage is { } usage ? new()
            {
                InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens
            } : null,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response.Root),
            Response = ResponseInfo(response, request.Model)
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        var payload = options.AdditionalProperties?.ToDictionary(
            item => item.Key, item => (object?)item.Value.Clone(), StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        ApplyStandardValues(payload, options);
        payload["model"] = NativeModel(options.Model);
        var response = await GenerateImageAsync(payload, cancellationToken);
        var images = response.Root.Deserialize<OpenAIImagesResponse>()!;
        images.OutputFormat ??= "webp";
        return images;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data!)
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json!, CreatedAt = response.Created, OutputFormat = response.OutputFormat,
                Size = response.Size, Usage = response.Usage
            };
    }

    private async Task<AIResponse> ExecuteImageAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var user = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
        if (user?.Content?.OfType<AIFileContentPart>().Any() == true)
            throw new NotSupportedException("Creora does not document image editing or reference images.");
        var prompt = string.Join("\n", user?.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
        var model = NativeModel(request.Model ?? throw new ArgumentException("An image model is required."));
        var payload = request.Metadata?.TryGetValue(GetIdentifier(), out var raw) == true
            ? CopyObject(raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw))
            : new(StringComparer.Ordinal);
        // Only top-level standard metadata is mapped; scoped native fields must not be coerced.
        foreach (var name in new[] { "n", "size" })
            if (request.Metadata?.TryGetValue(name, out var value) == true
                && value is not null && value is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
                payload[name] = value;
        payload["model"] = model;
        payload["prompt"] = prompt;
        var response = await GenerateImageAsync(payload, cancellationToken);
        var images = response.Root.Deserialize<OpenAIImagesResponse>()!;
        images.OutputFormat ??= "webp";
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
            Usage = images.Usage is { } usage ? new AIUsage
            {
                InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens
            } : null,
            Output = new AIOutput
            {
                Items = [new AIOutputItem
                {
                    Type = "message", Role = "assistant",
                    Content = [new AIToolCallContentPart
                    {
                        Type = "tool-call", ToolCallId = $"ig_{request.Id ?? Guid.NewGuid().ToString("N")}",
                        ToolName = "generate_image", Title = "Generate image", State = "output-available",
                        ProviderExecuted = true, Input = new { model, prompt },
                        Output = new CallToolResult
                        {
                            IsError = false,
                            Content = images.Data!.Select(image => (ContentBlock)ImageContentBlock.FromBytes(
                                Convert.FromBase64String(image.B64Json!), $"image/{images.OutputFormat}")).ToList(),
                            StructuredContent = response.Root
                        },
                        Metadata = new()
                        {
                            [GetIdentifier()] = new Dictionary<string, object>
                            {
                                ["response"] = response.Root, ["headers"] = response.Headers
                            }
                        }
                    }]
                }]
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamImageAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteImageAsync(request, cancellationToken);
        var usage = response.Usage as AIUsage;
        var items = response.Output?.Items ?? throw new InvalidOperationException("Creora image response has no output items.");
        foreach (var item in items)
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>())
            {
                var name = tool.ToolName ?? throw new InvalidOperationException("Creora image tool output has no name.");
                var metadata = tool.Metadata?.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value is Dictionary<string, object> values ? values
                        : throw new InvalidOperationException("Creora image tool metadata must be an object."));
                yield return ImageEvent("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
                {
                    ToolName = name, Input = tool.Input!, Title = tool.Title, ProviderExecuted = true,
                    ProviderMetadata = metadata
                });
                yield return ImageEvent("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
                {
                    ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true,
                    Preliminary = false, ProviderMetadata = metadata
                });
                yield return ImageEvent("finish", tool.ToolCallId, new AIFinishEventData
                {
                    FinishReason = "tool-calls", Model = request.Model,
                    InputTokens = (int?)usage?.InputTokens, OutputTokens = (int?)usage?.OutputTokens,
                    TotalTokens = (int?)usage?.TotalTokens
                });
            }
    }

    private AIStreamEvent ImageEvent(string type, string id, object data) => new()
    {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data }
    };

    private static object Unsupported(string feature)
        => new { type = "unsupported", feature, details = "Not documented by Creora; use native provider options for future API fields." };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            throw new NotSupportedException("Creora reference video inputs are not verified; no input media is downloaded.");
        var payload = CopyOptions(request.ProviderOptions);
        payload["model"] = NativeModel(request.Model);
        payload["prompt"] = request.Prompt;
        var response = await SendJsonAsync(HttpMethod.Post, "videos", payload, cancellationToken);
        var id = response.Root.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Creora video response missing id: {response.Root.GetRawText()}");
        var warnings = new List<object>();
        if (request.Duration is not null) warnings.Add(Unsupported("duration"));
        if (request.Resolution is not null) warnings.Add(Unsupported("resolution"));
        if (request.AspectRatio is not null) warnings.Add(Unsupported("aspectRatio"));
        if (request.Fps is not null) warnings.Add(Unsupported("fps"));
        if (request.N is not null) warnings.Add(Unsupported("n"));
        if (request.Seed is not null) warnings.Add(Unsupported("seed"));
        if (request.GenerateAudio is not null) warnings.Add(Unsupported("generateAudio"));
        return new()
        {
            Operation = id, Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response.Root),
            Response = ResponseInfo(response, request.Model)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var path = $"videos/{Uri.EscapeDataString(operation)}";
        var response = await SendJsonAsync(HttpMethod.Get, path, null, cancellationToken);
        var root = response.Root;
        var model = root.TryGetProperty("model", out var nativeModel) ? nativeModel.GetString()! : "creora-video";
        var info = ResponseInfo(response, model);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        switch (root.GetProperty("status").GetString())
        {
            case "queued":
            case "in_progress":
                return new VideoOperationPendingResult { Response = info, ProviderMetadata = metadata };
            case "failed":
                return new VideoOperationErrorResult
                {
                    Error = root.GetRawText(), Response = info, ProviderMetadata = metadata
                };
            case "completed":
                ApplyAuthHeader();
                using (var content = await _client.GetAsync(path + "/content", cancellationToken))
                {
                    var bytes = await content.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (!content.IsSuccessStatusCode)
                        EnsureSuccess(content, System.Text.Encoding.UTF8.GetString(bytes));
                    if (bytes.Length == 0)
                        throw new InvalidOperationException("Creora returned empty video content.");
                    return new VideoOperationCompletedResult
                    {
                        Videos = [new VideoOperationVideoData
                        {
                            Type = "base64", MediaType = content.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                            Data = Convert.ToBase64String(bytes)
                        }],
                        Response = info, ProviderMetadata = metadata
                    };
                }
            default:
                throw new InvalidOperationException($"Unknown Creora video status: {root.GetRawText()}");
        }
    }
}
