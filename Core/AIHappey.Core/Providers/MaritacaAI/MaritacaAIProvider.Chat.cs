using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Unified.Models;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.MaritacaAI;

public partial class MaritacaAIProvider
{
    private static readonly HashSet<string> IntegratedToolTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search",
        "code_interpreter",
        "code_execution",
        "data_ocean"
    };

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        if (UsesIntegratedTools(chatRequest))
        {
            var response = await ExecuteIntegratedToolsAsync(unifiedRequest, cancellationToken);

            await foreach (var streamEvent in StreamCompletedResponseAsync(unifiedRequest, response, cancellationToken))
            {
                foreach (var uiPart in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                    yield return uiPart;
            }

            yield break;
        }

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
    }

    private bool UsesIntegratedTools(ChatRequest request)
        => request.Tools?.Any(tool => IsIntegratedToolType(tool.Name)) == true
           || GetProviderMetadataTools(request)
               .Any(IsIntegratedToolType);

    private IEnumerable<string> GetProviderMetadataTools(ChatRequest request)
    {
        if (request.ProviderMetadata is null
            || !request.ProviderMetadata.TryGetValue(GetIdentifier(), out var providerMetadata)
            || providerMetadata.ValueKind != JsonValueKind.Object
            || !providerMetadata.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                continue;

            if (tool.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                yield return type.GetString() ?? string.Empty;
            else if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                yield return name.GetString() ?? string.Empty;
        }
    }

    private static bool IsIntegratedToolType(string? type)
        => !string.IsNullOrWhiteSpace(type) && IntegratedToolTypes.Contains(type);

    private async Task<AIResponse> ExecuteIntegratedToolsAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var responseRequest = request.ToResponseRequest(GetIdentifier());
        responseRequest.Stream = false;
        responseRequest.Store ??= false;
        NormalizeResponseInput(responseRequest);

        foreach (var tool in responseRequest.Tools ?? [])
        {
            var type = tool.Type;
            var name = tool.Extra is not null && tool.Extra.TryGetValue("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (IsIntegratedToolType(type))
                continue;

            if (IsIntegratedToolType(name))
            {
                tool.Type = ToMaritacaResponseToolType(name!);
                tool.Extra = null;
            }
        }

        var response = await ResponsesAsync(responseRequest, cancellationToken);
        return AddGeneratedFiles(response.ToUnifiedResponse(GetIdentifier()), response);
    }

    private static string ToMaritacaResponseToolType(string type)
        => string.Equals(type, "code_execution", StringComparison.OrdinalIgnoreCase)
            ? "code_interpreter"
            : type;

    private AIResponse AddGeneratedFiles(AIResponse unifiedResponse, Responses.ResponseResult response)
    {
        var files = GetGeneratedFiles(response).ToList();
        if (files.Count == 0)
            return unifiedResponse;

        var outputItems = unifiedResponse.Output?.Items?.ToList() ?? [];
        outputItems.Add(new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content = files.Select(file => new AIFileContentPart
            {
                Type = "file",
                MediaType = file.MediaType,
                Filename = file.Filename,
                Data = file.ContentBase64
            }).Cast<AIContentPart>().ToList()
        });

        return new AIResponse
        {
            ProviderId = unifiedResponse.ProviderId,
            Model = unifiedResponse.Model,
            Status = unifiedResponse.Status,
            Usage = unifiedResponse.Usage,
            Metadata = unifiedResponse.Metadata,
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = unifiedResponse.Output?.Metadata
            }
        };
    }

    private static IEnumerable<GeneratedFile> GetGeneratedFiles(Responses.ResponseResult response)
    {
        foreach (var output in response.Output ?? [])
        {
            var file = TryGetGeneratedFile(JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web));
            if (file is not null)
                yield return file;
        }

        if (response.AdditionalProperties?.TryGetValue("generated_files", out var generatedFiles) != true
            || generatedFiles.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var generatedFile in generatedFiles.EnumerateArray())
        {
            var file = TryGetGeneratedFile(generatedFile);
            if (file is not null)
                yield return file;
        }
    }

    private static GeneratedFile? TryGetGeneratedFile(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("content_base64", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new GeneratedFile(
            item.TryGetProperty("filename", out var filename) ? filename.GetString() : null,
            item.TryGetProperty("mime_type", out var mediaType) ? mediaType.GetString() ?? "application/octet-stream" : "application/octet-stream",
            content.GetString() ?? string.Empty);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamCompletedResponseAsync(
        AIRequest request,
        AIResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerId = GetIdentifier();
        var timestamp = DateTimeOffset.UtcNow;
        var textId = request.Id ?? Guid.NewGuid().ToString("N");
        var textParts = response.Output?.Items?
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content?.OfType<AITextContentPart>() ?? [])
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList() ?? [];

        if (textParts.Count > 0)
        {
            yield return CreateStreamEvent("text-start", textId, new AITextStartEventData());

            foreach (var text in textParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateStreamEvent("text-delta", textId, new AITextDeltaEventData { Delta = text });
            }

            yield return CreateStreamEvent("text-end", textId, new AITextEndEventData());
        }

        foreach (var file in response.Output?.Items?
                     .SelectMany(item => item.Content?.OfType<AIFileContentPart>() ?? []) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return CreateStreamEvent(
                "file",
                Guid.NewGuid().ToString("N"),
                new AIFileEventData
                {
                    MediaType = file.MediaType ?? "application/octet-stream",
                    Filename = file.Filename,
                    Url = ToDataUrl(file.Data?.ToString(), file.MediaType)
                });
        }

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = textId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model ?? request.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? request.Model ?? "unknown",
                        timestamp,
                        response.Usage,
                        additionalProperties: response.Metadata)
                }
            }
        };

        AIStreamEvent CreateStreamEvent(string type, string id, object data)
            => new()
            {
                ProviderId = providerId,
                Metadata = response.Metadata,
                Event = new AIEventEnvelope
                {
                    Type = type,
                    Id = id,
                    Timestamp = timestamp,
                    Data = data
                }
            };
    }

    private static string ToDataUrl(string? data, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(data) || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data ?? string.Empty;

        return $"data:{mediaType ?? "application/octet-stream"};base64,{data}";
    }

    private sealed record GeneratedFile(string? Filename, string MediaType, string ContentBase64);
}
