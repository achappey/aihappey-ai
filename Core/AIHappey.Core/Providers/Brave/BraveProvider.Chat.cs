using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Brave;

public partial class BraveProvider
{
    private const string CitationStartTag = "<citation>";
    private const string CitationEndTag = "</citation>";
    private const string EnumStartTag = "<enum_start>";
    private const string EnumStartEndTag = "</enum_start>";
    private const string EnumItemStartTag = "<enum_item>";
    private const string EnumItemEndTag = "</enum_item>";
    private const string EnumEndTag = "<enum_end>";
    private const string EnumEndEndTag = "</enum_end>";

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        var emittedSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedImageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                if (TryConvertEnumControlTextDelta(uiPart))
                    continue;

                if (uiPart is TextDeltaUIMessageStreamPart textPart
                    && TryParseEnumItemBlock(textPart.Delta, out var entity))
                {
                    await foreach (var entityPart in ConvertEnumEntityToUIPartsAsync(
                        textPart,
                        entity,
                        emittedSourceIds,
                        emittedImageUrls,
                        cancellationToken))
                    {
                        yield return entityPart;
                    }

                    continue;
                }

                if (TryConvertCitationTextDelta(uiPart, out var sourcePart))
                {
                    if (emittedSourceIds.Add(sourcePart.SourceId))
                        yield return sourcePart;

                    continue;
                }

                yield return uiPart;
            }
        }

        yield break;
    }

    private static bool TryConvertEnumControlTextDelta(UIMessagePart uiPart)
        => uiPart is TextDeltaUIMessageStreamPart textPart
            && !string.IsNullOrWhiteSpace(textPart.Delta)
            && (IsCompleteTaggedBlock(textPart.Delta, EnumStartTag, EnumStartEndTag)
                || IsCompleteTaggedBlock(textPart.Delta, EnumEndTag, EnumEndEndTag));

    private async IAsyncEnumerable<UIMessagePart> ConvertEnumEntityToUIPartsAsync(
        TextDeltaUIMessageStreamPart textPart,
        JsonElement entity,
        HashSet<string> emittedSourceIds,
        HashSet<string> emittedImageUrls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var originalTokens = GetString(entity, "original_tokens");
        if (!string.IsNullOrEmpty(originalTokens))
        {
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = textPart.Id,
                Delta = originalTokens,
                ProviderMetadata = textPart.ProviderMetadata
            };
        }

        if (TryCreateEntitySourcePart(entity, out var entitySourcePart)
            && emittedSourceIds.Add(entitySourcePart.SourceId))
        {
            yield return entitySourcePart;
        }

        foreach (var citationSourcePart in CreateEntityCitationSourceParts(entity))
        {
            if (emittedSourceIds.Add(citationSourcePart.SourceId))
                yield return citationSourcePart;
        }

        foreach (var imageUrl in EnumerateStringArray(entity, "images"))
        {
            if (!emittedImageUrls.Add(imageUrl))
                continue;

            var imagePart = await DownloadEntityImageFilePartAsync(entity, imageUrl, cancellationToken);
            if (imagePart is not null)
                yield return imagePart;
        }
    }

    private static bool TryConvertCitationTextDelta(UIMessagePart uiPart, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        if (uiPart is not TextDeltaUIMessageStreamPart textPart
            || string.IsNullOrWhiteSpace(textPart.Delta))
        {
            return false;
        }

        return TryParseCitationBlock(textPart.Delta, out sourcePart);
    }

    private static bool TryParseEnumItemBlock(string text, out JsonElement entity)
    {
        entity = default;

        if (!TryExtractCompleteTaggedPayload(text, EnumItemStartTag, EnumItemEndTag, out var payload))
            return false;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            entity = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseCitationBlock(string text, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        if (!TryExtractCompleteTaggedPayload(text, CitationStartTag, CitationEndTag, out var payload))
            return false;
        
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        JsonElement citation;
        try
        {
            using var document = JsonDocument.Parse(payload);
            citation = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (citation.ValueKind != JsonValueKind.Object)
            return false;

        var url = GetString(citation, "url");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var number = GetInt(citation, "number");
        var snippet = GetString(citation, "snippet");
        var title = GetString(citation, "title")
            ?? NormalizeTitle(snippet)
            ?? url;

        var metadata = new Dictionary<string, object>
        {
            ["raw"] = citation
        };

        AddIfNotNull(metadata, "number", number);
        AddIfNotNull(metadata, "start_index", GetInt(citation, "start_index"));
        AddIfNotNull(metadata, "end_index", GetInt(citation, "end_index"));
        AddIfNotNull(metadata, "favicon", GetString(citation, "favicon"));
        AddIfNotNull(metadata, "snippet", snippet);

        sourcePart = new SourceUIPart
        {
            SourceId = number is not null ? $"brave-citation-{number}" : url,
            Url = url,
            Title = title,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                ["brave"] = metadata
            }
        };

        return true;
    }

    private static bool TryCreateEntitySourcePart(JsonElement entity, out SourceUIPart sourcePart)
    {
        sourcePart = default!;

        var url = GetString(entity, "href");
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var uuid = GetString(entity, "uuid");
        var title = GetString(entity, "name") ?? url;
        var metadata = CreateEntityMetadata(entity);

        sourcePart = new SourceUIPart
        {
            SourceId = !string.IsNullOrWhiteSpace(uuid) ? $"brave-entity-{uuid}" : url,
            Url = url,
            Title = title,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                ["brave"] = metadata
            }
        };

        return true;
    }

    private static IEnumerable<SourceUIPart> CreateEntityCitationSourceParts(JsonElement entity)
    {
        if (!TryGetProperty(entity, "citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var citation in citations.EnumerateArray())
        {
            if (citation.ValueKind != JsonValueKind.Object)
                continue;

            var citationText = $"{CitationStartTag}{citation.GetRawText()}{CitationEndTag}";
            if (TryParseCitationBlock(citationText, out var sourcePart))
                yield return sourcePart;
        }
    }

    private async Task<FileUIPart?> DownloadEntityImageFilePartAsync(
        JsonElement entity,
        string imageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = GuessImageMediaTypeFromUrl(imageUrl) ?? MediaTypeNames.Image.Png;

            var metadata = CreateEntityImageMetadata(entity, imageUrl, mediaType);
            var filename = GetDownloadFileName(response, imageUrl, mediaType);

            return new FileUIPart
            {
                MediaType = mediaType,
                Url = Convert.ToBase64String(bytes),
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [GetIdentifier()] = metadata
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object> CreateEntityMetadata(JsonElement entity)
    {
        var metadata = new Dictionary<string, object>
        {
            ["raw"] = entity,
            ["kind"] = "entity"
        };

        AddIfNotNull(metadata, "uuid", GetString(entity, "uuid"));
        AddIfNotNull(metadata, "name", GetString(entity, "name"));
        AddIfNotNull(metadata, "href", GetString(entity, "href"));
        AddIfNotNull(metadata, "extra_text", GetString(entity, "extra_text"));
        AddIfNotNull(metadata, "original_tokens", GetString(entity, "original_tokens"));
        AddIfNotNull(metadata, "instance_of", GetRawClone(entity, "instance_of"));
        AddIfNotNull(metadata, "images", GetRawClone(entity, "images"));
        AddIfNotNull(metadata, "citations", GetRawClone(entity, "citations"));

        return metadata;
    }

    private static Dictionary<string, object> CreateEntityImageMetadata(JsonElement entity, string imageUrl, string mediaType)
    {
        var metadata = CreateEntityMetadata(entity);
        metadata["kind"] = "entity_image";
        metadata["origin_url"] = imageUrl;
        metadata["media_type"] = mediaType;
        return metadata;
    }

    private static bool TryExtractCompleteTaggedPayload(string text, string startTag, string endTag, out string payload)
    {
        payload = string.Empty;

        var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        var payloadStart = start + startTag.Length;
        var end = text.IndexOf(endTag, payloadStart, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return false;

        var before = text[..start];
        var after = text[(end + endTag.Length)..];
        if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            return false;

        payload = text[payloadStart..end];
        return true;
    }

    private static bool IsCompleteTaggedBlock(string text, string startTag, string endTag)
        => TryExtractCompleteTaggedPayload(text, startTag, endTag, out _);

    private static IEnumerable<string> EnumerateStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
        }
    }

    private static JsonElement? GetRawClone(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.Clone();
    }

    private static string? GetDownloadFileName(HttpResponseMessage response, string url, string mediaType)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName.Trim('"');

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        fileName = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return GuessImageFileExtension(mediaType) is { } extension
            ? $"brave-image{extension}"
            : null;
    }

    private static string? GuessImageMediaTypeFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return Path.GetExtension(uri.LocalPath).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private static string? GuessImageFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/jpg" => ".jpg",
            MediaTypeNames.Image.Gif => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => null
        };

    private static string? NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static void AddIfNotNull(Dictionary<string, object> metadata, string key, object? value)
    {
        if (value is not null)
            metadata[key] = value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
