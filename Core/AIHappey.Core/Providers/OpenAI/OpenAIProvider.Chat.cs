using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAI.Containers;
using AIHappey.Vercel.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
         ChatRequest chatRequest,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model.Type)
        {
            case "image":
                await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "speech":
                await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "video":
                await foreach (var p in this.StreamVideoAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
        }

        ApplyAuthHeader();

        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            var overriddenParts = await TryMapContainerFileCitationOverrideAsync(part.Event, cancellationToken);
            if (overriddenParts is { Count: > 0 })
            {
                foreach (var overriddenPart in overriddenParts)
                    yield return overriddenPart;

                continue;
            }

            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }
    }

    private async Task<List<UIMessagePart>?> TryMapContainerFileCitationOverrideAsync(
        AIEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.Type, "source-url", StringComparison.OrdinalIgnoreCase))
            return null;

        var data = TryGetJsonElementMap(envelope.Data);
        if (data is null)
            return null;

        var citationType = data.TryGetString("type");
        if (!string.Equals(citationType, "container_file_citation", StringComparison.OrdinalIgnoreCase))
            return null;

        var containerId = data.TryGetString("container_id");
        var fileId = data.TryGetString("file_id");

        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(fileId))
            return null;

        var filename = data.TryGetString("filename") ?? data.TryGetString("title");
        var canonicalOpenAiFileUrl = $"https://api.openai.com/v1/containers/{containerId}/files/{fileId}/content";

        var providerMetadata = new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = BuildContainerCitationMetadata(data, containerId, fileId, filename, canonicalOpenAiFileUrl)
        };

        var parts = new List<UIMessagePart>
        {
            new SourceUIPart
            {
                Url = canonicalOpenAiFileUrl,
                SourceId = canonicalOpenAiFileUrl,
                Title = filename ?? fileId,
                ProviderMetadata = providerMetadata
            }
        };

        try
        {
            var containerClient = new ContainerClient(GetKey());
            var content = await containerClient.DownloadContainerFileAsync(containerId, fileId, cancellationToken);

            var resolvedFilename = string.IsNullOrWhiteSpace(filename) ? $"{fileId}.bin" : filename;
            var contentTypeProvider = new FileExtensionContentTypeProvider();
            var contentType = contentTypeProvider.TryGetContentType(resolvedFilename, out var detected)
                ? detected
                : "application/octet-stream";

            var fileMetadata = new Dictionary<string, Dictionary<string, object>?>
            {
                [GetIdentifier()] = BuildContainerCitationMetadata(data, containerId, fileId, resolvedFilename, canonicalOpenAiFileUrl)
            };

            var filePart = new FileUIPart
            {
                MediaType = contentType,
                Url = Convert.ToBase64String(content.Value.ToArray()),
                ProviderMetadata = fileMetadata
            };

            parts.Add(filePart);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep stream resilient: always emit the replaced source part even when file download fails.
        }

        return parts;
    }

    private static Dictionary<string, JsonElement>? TryGetJsonElementMap(object? data)
    {
        if (data is null)
            return null;

        if (data is Dictionary<string, JsonElement> typed)
            return typed;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
                JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object> BuildContainerCitationMetadata(
        Dictionary<string, JsonElement> rawData,
        string containerId,
        string fileId,
        string? filename,
        string canonicalOpenAiFileUrl)
    {
        return new Dictionary<string, object>
        {
            ["type"] = "container_file_citation",
            ["container_id"] = containerId,
            ["file_id"] = fileId,
            ["filename"] = filename ?? string.Empty,
            ["openai_file_url"] = canonicalOpenAiFileUrl,
            ["raw"] = JsonSerializer.SerializeToElement(rawData, JsonSerializerOptions.Web)
        };
    }

}
