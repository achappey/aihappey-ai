using OpenAI.Responses;
using System.Text.Json.Serialization;
using System.Text.Json;
using OpenAI.Containers;
using Microsoft.AspNetCore.StaticFiles;
using System.Net.Mime;
using ModelContextProtocol.Protocol;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.OpenAI;

public static class ResponseExtensions
{
    public static ImageContentBlock ToImageContentBlock(this ImageGenerationCallResponseItem imageGenerationCallResponseItem) => new()
    {
        Data = Convert.ToBase64String(imageGenerationCallResponseItem.ImageResultBytes),
        MimeType = MediaTypeNames.Image.Png
    };

    public static FileUIPart ToFileUIPart(this ImageGenerationCallResponseItem imageGenerationCallResponseItem) => new()
    {
        Url = Convert.ToBase64String(imageGenerationCallResponseItem.ImageResultBytes),
        MediaType = MediaTypeNames.Image.Png
    };

    public sealed class ContainerFileCitation
    {
        [JsonPropertyName("type")] public string? Type { get; set; } = "container_file_citation";

        [JsonPropertyName("container_id")] public string? ContainerId { get; set; }

        [JsonPropertyName("end_index")] public int? EndIndex { get; set; }

        [JsonPropertyName("file_id")] public string? FileId { get; set; }

        [JsonPropertyName("filename")] public string? Filename { get; set; }

        [JsonPropertyName("start_index")] public int? StartIndex { get; set; }
    }

    public sealed class UrlCitation
    {
        [JsonPropertyName("type")] public string? Type { get; set; } = "url_citation";

        [JsonPropertyName("url")] public string? Url { get; set; }

        [JsonPropertyName("end_index")] public int? EndIndex { get; set; }

        [JsonPropertyName("title")] public string? Title { get; set; }

        [JsonPropertyName("start_index")] public int? StartIndex { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ToolCallDeltaPart ToToolCallDeltaPart(this string delta,
            string toolCallId) => new()
            {
                ToolCallId = toolCallId,
                InputTextDelta = delta,
            };

    public static ToolCallDeltaPart ToToolCallDeltaPart(this StreamingResponseFunctionCallArgumentsDeltaUpdate streamingResponseFunctionCallArgumentsDeltaUpdate,
        string toolCallId) => streamingResponseFunctionCallArgumentsDeltaUpdate.Delta.ToString().ToToolCallDeltaPart(toolCallId);

    public static ToolCallDeltaPart ToToolCallDeltaPart(this StreamingResponseCodeInterpreterCallCodeDeltaUpdate streamingResponseCodeInterpreterCallCodeDeltaUpdate)
        => streamingResponseCodeInterpreterCallCodeDeltaUpdate.Delta.EscapeJsonFragment().ToToolCallDeltaPart(streamingResponseCodeInterpreterCallCodeDeltaUpdate.ItemId);

    public static ToolCallPart ToToolCallDeltaPart(this StreamingResponseCodeInterpreterCallCodeDoneUpdate streamingResponseCodeInterpreterCallCodeDoneUpdate)
        => new()
        {
            ToolName = Constants.CodeInterpreter,
            ToolCallId = streamingResponseCodeInterpreterCallCodeDoneUpdate.ItemId,
            Input = new
            {
                code = streamingResponseCodeInterpreterCallCodeDoneUpdate.Code
            },
            ProviderExecuted = true
        };


    private static string EscapeJsonFragment(this string s)
    {
        var json = JsonSerializer.Serialize(s); // e.g. "\"print(\\\"hi\\\")\\n\""
        return json.Substring(1, json.Length - 2); // remove the surrounding quotes
    }

    public static async IAsyncEnumerable<UIMessagePart> GetSourceUiPartsFromCompleted(
        this StreamingResponseCompletedUpdate completed,
        ContainerClient openAIFileClient)
    {
        // We willen niet afhankelijk zijn van exacte SDK typeshape:
        // serialize de Response/Result terug naar JSON en loop output/content/annotations.
        object? responseObj =
            completed.GetType().GetProperty("Response")?.GetValue(completed) ??
            completed.GetType().GetProperty("Result")?.GetValue(completed);

        if (responseObj is null)
            yield break;

        JsonElement root = JsonSerializer.SerializeToElement(responseObj, _jsonOpts);

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String)
                continue;
            if (!string.Equals(itemType.GetString(), "message", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var content in contentArr.EnumerateArray())
            {
                if (!content.TryGetProperty("type", out var contentType) || contentType.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(contentType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!content.TryGetProperty("annotations", out var annArr) || annArr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var ann in annArr.EnumerateArray())
                {
                    await foreach (var part in BuildFromElement(ann, openAIFileClient))
                        yield return part;
                }
            }
        }
    }


    public static async IAsyncEnumerable<UIMessagePart> GetSourceUiPartsFromAnnotation(this StreamingResponseTextAnnotationAddedUpdate streamingResponseTextAnnotationAddedUpdate,
        ContainerClient openAIFileClient)
    {
        string annotation = streamingResponseTextAnnotationAddedUpdate.Annotation?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(annotation))
            yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(annotation); }
        catch { yield break; }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                await foreach (var part in BuildFromElement(doc.RootElement, openAIFileClient))
                    yield return part;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    await foreach (var part in BuildFromElement(el, openAIFileClient))
                        yield return part;
                }
            }
        }



    }


    static async IAsyncEnumerable<UIMessagePart> BuildFromElement(JsonElement el, ContainerClient containerClient)
    {
        string? type = null;
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("type", out var typeProp) &&
            typeProp.ValueKind == JsonValueKind.String)
        {
            type = typeProp.GetString();
        }

        // 1) container_file_citation
        if (string.Equals(type, "container_file_citation", StringComparison.OrdinalIgnoreCase))
        {
            ContainerFileCitation? cfc = null;
            try { cfc = el.Deserialize<ContainerFileCitation>(_jsonOpts); } catch { /* ignore */ }
            if (cfc == null || string.IsNullOrWhiteSpace(cfc.FileId)) yield break;

            var url = $"https://api.openai.com/v1/containers/{cfc.ContainerId}/files/{cfc.FileId}/content";

            yield return new SourceUIPart
            {
                Url = url,
                Title = string.IsNullOrWhiteSpace(cfc.Filename) ? null : cfc.Filename,
                SourceId = url
            };

            yield return ToolCallPart.CreateProviderExecuted(cfc.FileId,
                "download_container_file", new
                {
                    cfc.ContainerId,
                    cfc.FileId
                });

            var content = await containerClient.DownloadContainerFileAsync(cfc.ContainerId,
                cfc.FileId);

            var provider = new FileExtensionContentTypeProvider();

            if (!provider.TryGetContentType(cfc.Filename!, out var contentType))
            {
                // default/fallback
                contentType = "application/octet-stream";
            }

            yield return new ToolOutputAvailablePart()
            {
                ToolCallId = cfc.FileId,
                ProviderExecuted = true,
                Output = new CallToolResult()
                {
                    Content = [new EmbeddedResourceBlock() {
                        Resource = new BlobResourceContents() {
                            Uri = $"file://{cfc.Filename!}",
                            Blob = Convert.ToBase64String(content.Value),
                            MimeType = contentType,
                        }
                      }]
                }
            };

            yield return content.Value.ToArray()
                .ToFileUIPart(contentType);

            yield break;
        }

        // 2) url_citation
        if (string.Equals(type, "url_citation", StringComparison.OrdinalIgnoreCase) || type is null)
        {
            // Try to interpret as a URL citation (even if type is missing)
            UrlCitation? uc = null;
            try { uc = el.Deserialize<UrlCitation>(_jsonOpts); } catch { /* ignore */ }

            // Be tolerant to the older property name "Urk"
            var url = uc?.Url;
            if (string.IsNullOrWhiteSpace(url) && el.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                url = urlProp.GetString();

            if (!string.IsNullOrWhiteSpace(url))
            {
                string? title = uc?.Title;
                if (string.IsNullOrWhiteSpace(title) && el.TryGetProperty("title", out var ttlProp) && ttlProp.ValueKind == JsonValueKind.String)
                    title = ttlProp.GetString();

                yield return new SourceUIPart
                {
                    Url = url!,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title,
                    SourceId = url!
                };
                yield break;
            }
        }

        // 3) Fallbacks: accept objects that simply have file_id or url
        if (el.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.String)
        {
            var fileId = fid.GetString();
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                var url = $"{Constants.OpenAIFilesAPI}{fileId}/content";
                string? title = null;
                if (el.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                    title = fn.GetString();

                yield return new SourceUIPart { Url = url, Title = string.IsNullOrWhiteSpace(title) ? null : title, SourceId = url };
            }
            yield break;
        }
        if (el.TryGetProperty("url", out var up) && up.ValueKind == JsonValueKind.String)
        {
            var url = up.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                string? title = null;
                if (el.TryGetProperty("title", out var tp) && tp.ValueKind == JsonValueKind.String)
                    title = tp.GetString();

                yield return new SourceUIPart { Url = url!, Title = string.IsNullOrWhiteSpace(title) ? null : title, SourceId = url! };
            }
        }
    }

}
