using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public sealed class ImageFileJsonConverter : JsonConverter<ImageFile>
{
    public override bool CanConvert(Type typeToConvert) => typeof(ImageFile).IsAssignableFrom(typeToConvert);

    public override ImageFile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = MediaFileJson.ReadType(root, options);
        if (type == "url")
            return new ImageFileUrl { Url = MediaFileJson.RequiredString(root, "url", options) };

        return new ImageFile
        {
            Type = type,
            Data = MediaFileJson.RequiredString(root, "data", options),
            MediaType = type == "file" ? MediaFileJson.RequiredString(root, "mediaType", options)
                : MediaFileJson.OptionalString(root, "mediaType", options)
        };
    }

    public override void Write(Utf8JsonWriter writer, ImageFile value, JsonSerializerOptions options)
        => MediaFileJson.Write(writer, value.Type, value is ImageFileUrl url ? url.Url : null,
            value.MediaType, value.Data);
}

public sealed class VideoFileJsonConverter : JsonConverter<VideoFile>
{
    public override bool CanConvert(Type typeToConvert) => typeof(VideoFile).IsAssignableFrom(typeToConvert);

    public override VideoFile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = MediaFileJson.ReadType(root, options);
        if (type == "url")
            return new VideoFileUrl { Url = MediaFileJson.RequiredString(root, "url", options) };

        return new VideoFile
        {
            Type = type,
            Data = MediaFileJson.RequiredString(root, "data", options),
            MediaType = type == "file" ? MediaFileJson.RequiredString(root, "mediaType", options)
                : MediaFileJson.OptionalString(root, "mediaType", options)
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoFile value, JsonSerializerOptions options)
        => MediaFileJson.Write(writer, value.Type, value is VideoFileUrl url ? url.Url : null,
            value.MediaType, value.Data);
}

internal static class MediaFileJson
{
    internal static IEnumerable<ValidationResult> Validate(string type, string? url, string? mediaType, string? data)
    {
        if (type == "url")
        {
            if (string.IsNullOrWhiteSpace(url))
                yield return new ValidationResult("The Url field is required for URL inputs.", ["Url"]);
            yield break;
        }
        if (type is not ("file" or "file_id" or "fileId"))
        {
            yield return new ValidationResult($"Unsupported media input type '{type}'.", ["Type"]);
            yield break;
        }
        if (string.IsNullOrWhiteSpace(data))
            yield return new ValidationResult("The Data field is required for inline files.", ["Data"]);
        if (type == "file" && string.IsNullOrWhiteSpace(mediaType))
            yield return new ValidationResult("The MediaType field is required for inline files.", ["MediaType"]);
    }

    internal static string ReadType(JsonElement root, JsonSerializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Media input must be an object.");

        // Older callers omit the discriminator for inline files.
        var type = OptionalString(root, "type", options) ?? "file";
        if (type is not ("file" or "url" or "file_id" or "fileId"))
            throw new JsonException($"Unsupported media input type '{type}'.");
        return type;
    }

    internal static string RequiredString(JsonElement root, string name, JsonSerializerOptions options)
        => OptionalString(root, name, options) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new JsonException($"Media input requires a non-empty '{name}'.");

    internal static string? OptionalString(JsonElement root, string name, JsonSerializerOptions options)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, options.PropertyNameCaseInsensitive
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Null)
                return null;
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException($"Media input '{name}' must be a string.");
            return property.Value.GetString();
        }
        return null;
    }

    internal static void Write(Utf8JsonWriter writer, string type, string? url, string? mediaType, string? data)
    {
        var error = Validate(type, url, mediaType, data).FirstOrDefault();
        if (error is not null)
            throw new JsonException(error.ErrorMessage);
        writer.WriteStartObject();
        writer.WriteString("type", type);
        if (type == "url")
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new JsonException("URL media input requires a non-empty 'url'.");
            writer.WriteString("url", url);
        }
        else
        {
            writer.WriteString("mediaType", mediaType);
            writer.WriteString("data", data);
        }
        writer.WriteEndObject();
    }
}
