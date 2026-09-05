using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    private const string RekaVideoFilenameMarker = "__aihappey_reka_video__";


    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

    private static AIRequest NormalizeRekaVideoInputs(AIRequest request)
    {
        if (request.Input?.Items is not { Count: > 0 } items
            || !items.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>()
                .Any(IsRekaVideoFile))
        {
            return request;
        }

        return new AIRequest
        {
            ProviderId = request.ProviderId,
            Model = request.Model,
            Id = request.Id,
            Instructions = request.Instructions,
            Input = new AIInput
            {
                Text = request.Input.Text,
                Metadata = request.Input.Metadata,
                Items = [.. items.Select(CloneRekaInputItem)]
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
            Tools = request.Tools,
            Metadata = request.Metadata,
            Headers = request.Headers,
            Verbosity = request.Verbosity
        };
    }

    private static AIInputItem CloneRekaInputItem(AIInputItem item)
        => new()
        {
            Type = item.Type,
            Id = item.Id,
            Role = item.Role,
            Metadata = item.Metadata,
            Content = item.Content is null
                ? null
                : [.. item.Content.Select(part => part is AIFileContentPart file && IsRekaVideoFile(file)
                    ? NormalizeRekaVideoFile(file)
                    : part)]
        };

    private static bool IsRekaVideoFile(AIFileContentPart file)
        => file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private static AIFileContentPart NormalizeRekaVideoFile(AIFileContentPart file)
    {
        var mediaType = file.MediaType ?? "video/mp4";
        var value = RekaMediaValue(file.Data);

        if (!IsAbsoluteOrDataUrl(value))
            value = $"data:{mediaType};base64,{value}";

        return new AIFileContentPart
        {
            Type = "file",
            MediaType = mediaType,
            Filename = RekaVideoFilenameMarker + (file.Filename ?? string.Empty),
            Data = value,
            Metadata = file.Metadata
        };
    }

    private static string RekaMediaValue(object? data)
        => data switch
        {
            null => string.Empty,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? string.Empty,
            _ => data.ToString() ?? string.Empty
        };

    private static bool IsAbsoluteOrDataUrl(string value)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https");

    private static void NormalizeRekaVideoParts(ChatCompletionOptions options)
    {
        foreach (var message in options.Messages)
        {
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                || message.Content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var changed = false;
            var parts = new List<object?>();

            foreach (var part in message.Content.EnumerateArray())
            {
                if (TryNormalizeRekaVideoPart(part, out var normalized))
                {
                    parts.Add(normalized);
                    changed = true;
                }
                else
                {
                    parts.Add(part.Clone());
                }
            }

            if (changed)
                message.Content = JsonSerializer.SerializeToElement(parts, JsonSerializerOptions.Web);
        }
    }

    private static bool TryNormalizeRekaVideoPart(JsonElement part, out object? normalized)
    {
        normalized = null;
        if (part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var type)
            || type.GetString() != "file"
            || !part.TryGetProperty("file", out var file)
            || file.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var filename = file.TryGetProperty("filename", out var filenameElement)
            ? filenameElement.GetString()
            : null;
        var value = file.TryGetProperty("file_data", out var dataElement)
            ? RekaMediaValue(dataElement)
            : string.Empty;

        var markedVideo = filename?.StartsWith(RekaVideoFilenameMarker, StringComparison.Ordinal) == true;
        var videoDataUrl = value.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase);
        if (!markedVideo && !videoDataUrl)
            return false;

        normalized = new { type = "video_url", video_url = value };
        return true;
    }

}
