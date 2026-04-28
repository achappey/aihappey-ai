using System.Text.Json;
using AIHappey.Responses.Streaming;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static IEnumerable<AIHappey.Unified.Models.AIEventEnvelope> CreateCodeInterpreterOutputFileEnvelopes(
        string providerId,
        ResponseOutputItemDone done)
    {
        if (done.Item.AdditionalProperties?.TryGetValue("outputs", out var outputs) != true
            || outputs.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var outputIndex = 0;
        foreach (var output in outputs.EnumerateArray())
        {
            if (!IsLogsOutput(output)
                || !TryGetString(output, "logs", out var logs)
                || string.IsNullOrWhiteSpace(logs)
                || !TryParseJsonObject(logs, out var logsJson)
                || !logsJson.RootElement.TryGetProperty("output_files", out var outputFiles)
                || outputFiles.ValueKind != JsonValueKind.Array)
            {
                outputIndex++;
                continue;
            }

            using (logsJson)
            {
                var fileIndex = 0;
                foreach (var outputFile in outputFiles.EnumerateArray())
                {
                    if (TryCreateCodeInterpreterOutputFileEnvelope(
                            providerId,
                            done,
                            outputFile,
                            outputIndex,
                            fileIndex) is { } envelope)
                    {
                        yield return envelope;
                    }

                    fileIndex++;
                }
            }

            outputIndex++;
        }
    }

    private static AIHappey.Unified.Models.AIEventEnvelope? TryCreateCodeInterpreterOutputFileEnvelope(
        string providerId,
        ResponseOutputItemDone done,
        JsonElement outputFile,
        int outputIndex,
        int fileIndex)
    {
        if (outputFile.ValueKind != JsonValueKind.Object
            || !TryGetByteArray(outputFile, "data", out var bytes)
            || bytes.Length == 0)
        {
            return null;
        }

        var mediaType = TryGetString(outputFile, "mime_type", out var mimeTypeValue) && !string.IsNullOrWhiteSpace(mimeTypeValue)
            ? mimeTypeValue
            : "application/octet-stream";

        var filename = TryGetString(outputFile, "file_name", out var fileNameValue) && !string.IsNullOrWhiteSpace(fileNameValue)
            ? fileNameValue
            : null;

        var filePath = TryGetString(outputFile, "file_path", out var filePathValue) && !string.IsNullOrWhiteSpace(filePathValue)
            ? filePathValue
            : null;

        var size = TryGetInt64(outputFile, "size", out var sizeValue)
            ? sizeValue
            : (long?)null;

        var idSuffix = !string.IsNullOrWhiteSpace(filename)
            ? filename
            : filePath ?? fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return CreateFileEnvelope(
            $"{done.Item.Id ?? string.Empty}:output_file:{fileIndex}",
            mediaType,
            $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}",
            filename,
            CreateProviderMetadata(providerId, new Dictionary<string, object?>
            {
                ["tool_name"] = "code_interpreter",
                ["item_id"] = done.Item.Id,
                ["output_index"] = done.OutputIndex,
                ["logs_output_index"] = outputIndex,
                ["output_file_index"] = fileIndex,
                ["file_name"] = filename,
                ["filename"] = filename,
                ["file_path"] = filePath,
                ["media_type"] = mediaType,
                ["mime_type"] = mediaType,
                ["size"] = size,
                ["source"] = "code_interpreter_call.output_files",
                ["id_hint"] = idSuffix
            }));
    }

    private static bool IsLogsOutput(JsonElement output)
        => output.ValueKind == JsonValueKind.Object
           && TryGetString(output, "type", out var type)
           && string.Equals(type, "logs", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJsonObject(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return true;

            document.Dispose();
        }
        catch
        {
            // Output logs are provider-supplied; malformed logs must not break streaming.
        }

        document = null!;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();

        return value is not null;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt64(out value);

        return property.ValueKind == JsonValueKind.String
               && long.TryParse(property.GetString(), out value);
    }

    private static bool TryGetByteArray(JsonElement element, string propertyName, out byte[] bytes)
    {
        bytes = [];

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            try
            {
                bytes = Convert.FromBase64String(property.GetString() ?? string.Empty);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (property.ValueKind != JsonValueKind.Array)
            return false;

        var result = new List<byte>();
        foreach (var value in property.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var number)
                || number is < byte.MinValue or > byte.MaxValue)
            {
                bytes = [];
                return false;
            }

            result.Add((byte)number);
        }

        bytes = [.. result];
        return true;
    }
}
