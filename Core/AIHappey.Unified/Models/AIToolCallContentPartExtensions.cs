using System.Text.Json;

namespace AIHappey.Unified.Models;

public static class AIToolCallContentPartExtensions
{
    private const string DownloadFileToolName = "download_file";
    private const string UploadFilesToolName = "upload_files";

    public static bool IsSyntheticProviderExecutedFileTransfer(this AIToolCallContentPart toolPart)
    {
        ArgumentNullException.ThrowIfNull(toolPart);

        if (!toolPart.IsProviderToolCall)
            return false;

        return IsSyntheticFileTransferToolName(toolPart.ToolName)
               || IsSyntheticFileTransferToolName(toolPart.Title)
               || ContainsEnabledFileTransferToolMarker(toolPart.Metadata);
    }

    private static bool IsSyntheticFileTransferToolName(string? value)
        => string.Equals(value, DownloadFileToolName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, UploadFilesToolName, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsEnabledFileTransferToolMarker(object? value)
    {
        if (value is JsonElement json)
            return ContainsEnabledFileTransferToolMarker(json);

        if (value is IEnumerable<KeyValuePair<string, object?>> entries)
        {
            foreach (var entry in entries)
            {
                if (IsFileTransferToolMarker(entry.Key)
                    && IsTrue(entry.Value))
                {
                    return true;
                }

                if (ContainsEnabledFileTransferToolMarker(entry.Value))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsEnabledFileTransferToolMarker(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (IsFileTransferToolMarker(property.Name)
                    && IsTrue(property.Value))
                {
                    return true;
                }

                if (ContainsEnabledFileTransferToolMarker(property.Value))
                    return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ContainsEnabledFileTransferToolMarker(item))
                    return true;
            }
        }

        return false;
    }

    private static bool IsFileTransferToolMarker(string key)
        => string.Equals(key, "download_tool", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "upload_tool", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(object? value)
        => value switch
        {
            true => true,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false
        };
}
