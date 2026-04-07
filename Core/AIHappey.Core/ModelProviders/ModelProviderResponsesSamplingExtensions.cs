using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Responses;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesSamplingExtensions
{
    public static Task<CreateMessageResult> ResponsesSamplingAsync(
        this IModelProvider modelProvider,
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
        => modelProvider.ResponsesSamplingAsync(
            chatRequest,
            requestFactory: null,
            requestMutator: null,
            outputContentMapper: null,
            cancellationToken);

    public static async Task<CreateMessageResult> ResponsesSamplingAsync(
        this IModelProvider modelProvider,
        CreateMessageRequestParams chatRequest,
        Func<CreateMessageRequestParams, CancellationToken, ValueTask<ResponseRequest>>? requestFactory,
        Action<ResponseRequest>? requestMutator,
        Func<JsonElement, IEnumerable<ContentBlock>?>? outputContentMapper,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = chatRequest.GetModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new Exception("No model provided.");

        var request = requestFactory != null
            ? await requestFactory(chatRequest, cancellationToken)
            : chatRequest.ToResponsesSamplingRequest();

        request.Model ??= model;
        request.Stream ??= false;
        request.Store ??= false;
        requestMutator?.Invoke(request);

        var response = await modelProvider.ResponsesAsync(request, cancellationToken);
        var content = ExtractContent(response, outputContentMapper);
        var meta = BuildMeta(response);

        return new CreateMessageResult
        {
            Model = string.IsNullOrWhiteSpace(response.Model) ? request.Model.ToModelId(modelProvider.GetIdentifier())
                : response.Model.ToModelId(modelProvider.GetIdentifier()),
            StopReason = ToStopReason(response.Status),
            Content = content.Count != 0 ? content : [string.Empty.ToTextContentBlock()],
            Role = Role.Assistant,
            Meta = meta
        };
    }

    public static ResponseRequest ToResponsesSamplingRequest(this CreateMessageRequestParams chatRequest)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = chatRequest.GetModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new Exception("No model provided.");

        var inputItems = new List<ResponseInputItem>();

        if (!string.IsNullOrWhiteSpace(chatRequest.SystemPrompt))
        {
            inputItems.Add(new ResponseInputMessage
            {
                Role = ResponseRole.System,
                Content = new ResponseMessageContent(chatRequest.SystemPrompt)
            });
        }

        foreach (var message in chatRequest.Messages)
            inputItems.Add(message.ToResponseInputMessage());

        return new ResponseRequest
        {
            Model = model,
            Instructions = null,
            Temperature = chatRequest.Temperature,
            MaxOutputTokens = chatRequest.MaxTokens,
            Truncation = TruncationStrategy.Auto,
            ParallelToolCalls = false,
            Metadata = chatRequest.Metadata.ToObjectDictionary(),
            Store = false,
            Stream = false,
            Input = new ResponseInput(inputItems)
        };
    }

    private static ResponseInputMessage ToResponseInputMessage(this SamplingMessage samplingMessage)
    {
        var role = samplingMessage.Role switch
        {
            Role.User => ResponseRole.User,
            Role.Assistant => ResponseRole.Assistant,
            _ => throw new NotSupportedException($"Unsupported role: {samplingMessage.Role}")
        };

        var parts = samplingMessage.Content
            .Select(ToResponseContentPart)
            .OfType<ResponseContentPart>()
            .ToArray();

        return new ResponseInputMessage
        {
            Role = role,
            Content = parts.Length == 0
                ? new ResponseMessageContent(samplingMessage.ToText() ?? string.Empty)
                : new ResponseMessageContent(parts)
        };
    }

    private static ResponseContentPart? ToResponseContentPart(ContentBlock contentBlock)
        => contentBlock switch
        {
            TextContentBlock text => new InputTextPart(text.Text),
            ImageContentBlock image => new InputImagePart
            {
                ImageUrl = image.ToDataUrl(),
                Detail = "auto"
            },
            EmbeddedResourceBlock embedded => ToResponseContentPart(embedded),
            _ => null
        };

    private static ResponseContentPart? ToResponseContentPart(EmbeddedResourceBlock embedded)
    {
        if (embedded.Resource is TextResourceContents text)
        {
            var value = string.IsNullOrWhiteSpace(text.Uri)
                ? text.Text
                : $"{text.Uri}:\n\n{text.Text}";

            return new InputTextPart(value);
        }

        if (embedded.Resource is BlobResourceContents blob)
        {
            return new InputFilePart
            {
                Filename = TryGetFileName(blob.Uri),
                FileData = Convert.ToBase64String(blob.Blob.ToArray())
            };
        }

        return null;
    }

    private static List<ContentBlock> ExtractContent(
        ResponseResult response,
        Func<JsonElement, IEnumerable<ContentBlock>?>? outputContentMapper)
    {
        List<ContentBlock> content = [];

        foreach (var item in response.Output ?? [])
        {
            var itemElement = JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web);

            if (itemElement.ValueKind != JsonValueKind.Object)
                continue;

            if (!itemElement.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (outputContentMapper?.Invoke(part) is { } mapped)
                    content.AddRange(mapped);

                content.AddRange(ToSamplingContentBlocks(part));
            }
        }

        return content;
    }

    private static IEnumerable<ContentBlock> ToSamplingContentBlocks(JsonElement part)
    {
        var type = GetString(part, "type");
        if (string.IsNullOrWhiteSpace(type))
            yield break;

        switch (type)
        {
            case "output_text":
            case "text":
            case "input_text":
            case "refusal":
            case "output_refusal":
                var text = GetString(part, "text")
                    ?? GetString(part, "refusal")
                    ?? GetString(part, "content");

                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.ToTextContentBlock();
                yield break;

            case "input_image":
            case "output_image":
            case "image":
                if (TryCreateImageBlock(part) is { } image)
                    yield return image;
                yield break;

            /*     case "input_audio":
                 case "output_audio":
                 case "audio":
                     if (TryCreateAudioBlock(part) is { } audio)
                         yield return audio;
                     yield break;*/

            case "input_file":
            case "output_file":
            case "file":
                if (TryCreateEmbeddedResourceBlock(part) is { } file)
                    yield return file;
                yield break;
        }

        if (TryCreateEmbeddedResourceBlock(part) is { } embedded)
            yield return embedded;
    }

    private static ContentBlock? TryCreateImageBlock(JsonElement part)
    {
        var imageUrl = GetString(part, "image_url")
            ?? GetNestedString(part, "image", "image_url")
            ?? GetNestedString(part, "image", "url")
            ?? GetNestedString(part, "result", "image_url")
            ?? GetNestedString(part, "result", "url");

        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        if (!TryParseDataUrl(imageUrl, out var mimeType, out var bytes))
            return null;

        return new ImageContentBlock
        {
            MimeType = mimeType ?? "image/png",
            Data = bytes
        };
    }

    private static ContentBlock? TryCreateAudioBlock(JsonElement part)
    {
        var data = GetString(part, "data")
            ?? GetNestedString(part, "audio", "data")
            ?? GetNestedString(part, "input_audio", "data");

        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            return new AudioContentBlock
            {
                MimeType = GetAudioMimeType(part),
                Data = Convert.FromBase64String(data)
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ContentBlock? TryCreateEmbeddedResourceBlock(JsonElement part)
    {
        var fileData = GetString(part, "file_data")
            ?? GetNestedString(part, "file", "file_data")
            ?? GetString(part, "data")
            ?? GetNestedString(part, "blob", "data");

        var mimeType = GetString(part, "mime_type")
            ?? GetNestedString(part, "file", "mime_type")
            ?? GetNestedString(part, "blob", "mime_type")
            ?? "application/octet-stream";

        var uri = GetString(part, "file_url")
            ?? GetNestedString(part, "file", "file_url")
            ?? GetString(part, "url")
            ?? GetString(part, "file_id")
            ?? GetNestedString(part, "file", "file_id")
            ?? "responses://file";

        if (string.IsNullOrWhiteSpace(fileData))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(fileData);
            if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = uri,
                        MimeType = mimeType,
                        Text = Encoding.UTF8.GetString(bytes)
                    }
                };
            }

            return new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = uri,
                    MimeType = mimeType,
                    Blob = bytes
                }
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static JsonObject? BuildMeta(ResponseResult response)
    {
        JsonObject meta = [];

        if (response.Metadata != null)
        {
            foreach (var pair in response.Metadata)
            {
                meta[pair.Key] = pair.Value is null
                    ? null
                    : JsonSerializer.SerializeToNode(pair.Value, JsonSerializerOptions.Web);
            }
        }

        if (response.Usage is not null)
        {
            var usage = JsonSerializer.SerializeToElement(response.Usage, JsonSerializerOptions.Web);
            AddUsage(meta, usage, "input_tokens", "inputTokens");
            AddUsage(meta, usage, "prompt_tokens", "inputTokens");
            AddUsage(meta, usage, "output_tokens", "outputTokens");
            AddUsage(meta, usage, "completion_tokens", "outputTokens");
            AddUsage(meta, usage, "total_tokens", "totalTokens");
        }

        if (response.Error is not null)
        {
            meta["error"] = JsonSerializer.SerializeToNode(response.Error, JsonSerializerOptions.Web);
        }

        return meta.Count == 0 ? null : meta;
    }

    private static void AddUsage(JsonObject meta, JsonElement usage, string sourceProperty, string targetProperty)
    {
        if (meta[targetProperty] is not null)
            return;

        if (usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(sourceProperty, out var property)
            && property.ValueKind == JsonValueKind.Number)
        {
            meta[targetProperty] = property.GetInt32();
        }
    }

    private static string ToStopReason(string? status)
        => string.IsNullOrWhiteSpace(status) ? "stop" : status switch
        {
            "completed" => "stop",
            "incomplete" => "maxTokens",
            "failed" => "error",
            "cancelled" => "cancelled",
            _ => status
        };

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
            return null;

        return GetString(nested, propertyName);
    }

    private static bool TryParseDataUrl(string value, out string? mimeType, out byte[] bytes)
    {
        mimeType = null;
        bytes = [];

        const string base64Marker = ";base64,";
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var markerIndex = value.IndexOf(base64Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        mimeType = value[5..markerIndex];
        var payload = value[(markerIndex + base64Marker.Length)..];

        try
        {
            bytes = Convert.FromBase64String(payload);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string GetAudioMimeType(JsonElement part)
    {
        var format = GetString(part, "format")
            ?? GetNestedString(part, "audio", "format")
            ?? GetNestedString(part, "input_audio", "format");

        return string.IsNullOrWhiteSpace(format)
            ? "audio/wav"
            : format.Contains('/') ? format : $"audio/{format}";
    }

    private static string? TryGetFileName(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        try
        {
            return Path.GetFileName(uri);
        }
        catch
        {
            return null;
        }
    }
}
