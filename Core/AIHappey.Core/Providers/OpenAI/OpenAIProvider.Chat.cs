using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }
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
            ["tool_name"] = "download_file",
            ["name"] = "download_file",
            ["download_tool"] = true,
            ["container_id"] = containerId,
            ["file_id"] = fileId,
            ["filename"] = filename ?? string.Empty,
            ["openai_file_url"] = canonicalOpenAiFileUrl,
            ["raw"] = JsonSerializer.SerializeToElement(rawData, JsonSerializerOptions.Web)
        };
    }

}
