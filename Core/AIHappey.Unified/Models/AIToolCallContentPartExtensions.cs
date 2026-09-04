using System.Text.Json;

namespace AIHappey.Unified.Models;

public static class AIToolCallContentPartExtensions
{
    private const string DownloadFileToolName = "download_file";
    private const string UploadFilesToolName = "upload_files";
    private const string GenerateVideoToolName = "generate_video";
    private const string GenerateSpeechToolName = "generate_speech";

    public static bool IsSyntheticProviderExecutedFileTransfer(this AIToolCallContentPart toolPart)
    {
        ArgumentNullException.ThrowIfNull(toolPart);

        if (!toolPart.IsProviderToolCall)
            return false;

        return IsSyntheticFileTransferToolName(toolPart.ToolName)
               || IsSyntheticFileTransferToolName(toolPart.Title)
               || ContainsEnabledFileTransferToolMarker(toolPart.Metadata);
    }

    public static bool IsSyntheticProviderExecutedGeneratedMedia(this AIToolCallContentPart toolPart)
    {
        ArgumentNullException.ThrowIfNull(toolPart);

        if (!toolPart.IsProviderToolCall)
            return false;

        return IsSyntheticGeneratedMediaToolName(toolPart.ToolName)
               || IsSyntheticGeneratedMediaToolName(toolPart.Title)
               || ContainsEnabledMarker(toolPart.Metadata, IsGeneratedMediaToolMarker);
    }

    public static bool IsSyntheticProviderExecutedReplayArtifact(this AIToolCallContentPart toolPart)
        => toolPart.IsSyntheticProviderExecutedFileTransfer()
           || toolPart.IsSyntheticProviderExecutedGeneratedMedia();

    private static bool IsSyntheticFileTransferToolName(string? value)
        => string.Equals(value, DownloadFileToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, UploadFilesToolName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSyntheticGeneratedMediaToolName(string? value)
        => string.Equals(value, GenerateVideoToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, GenerateSpeechToolName, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsEnabledFileTransferToolMarker(object? value)
        => ContainsEnabledMarker(value, IsFileTransferToolMarker);

    private static bool ContainsEnabledMarker(object? value, Func<string, bool> isMarker)
    {
        if (value is JsonElement json)
            return ContainsEnabledMarker(json, isMarker);

        if (value is IEnumerable<KeyValuePair<string, object?>> entries)
        {
            foreach (var entry in entries)
            {
                if (isMarker(entry.Key)
                    && IsTrue(entry.Value))
                {
                    return true;
                }

                if (ContainsEnabledMarker(entry.Value, isMarker))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsEnabledMarker(JsonElement value, Func<string, bool> isMarker)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (isMarker(property.Name)
                    && IsTrue(property.Value))
                {
                    return true;
                }

                if (ContainsEnabledMarker(property.Value, isMarker))
                    return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ContainsEnabledMarker(item, isMarker))
                    return true;
            }
        }

        return false;
    }

    private static bool IsFileTransferToolMarker(string key)
        => string.Equals(key, "download_tool", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "upload_tool", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedMediaToolMarker(string key)
        => string.Equals(key, "synthetic_generated_media", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "interactions.synthetic_generated_media", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(object? value)
        => value switch
        {
            true => true,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false
        };
}
