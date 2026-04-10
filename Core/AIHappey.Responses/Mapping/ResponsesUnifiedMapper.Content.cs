using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static IEnumerable<AIContentPart> ToUnifiedContentParts(ResponseMessageContent content)
    {
        if (content.IsText && !string.IsNullOrWhiteSpace(content.Text))
        {
            yield return new AITextContentPart { Type = "text", Text = content.Text! };
            yield break;
        }

        if (!content.IsParts || content.Parts is null)
            yield break;

        foreach (var part in content.Parts)
        {
            switch (part)
            {
                case InputTextPart inputText:
                    yield return new AITextContentPart { Type = "text", Text = inputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "input_text" } };
                    break;
                case OutputTextPart outputText:
                    yield return new AITextContentPart { Type = "text", Text = outputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "output_text", ["responses.annotations"] = outputText.Annotations } };
                    break;
                case InputImagePart image:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(image.ImageUrl),
                        Filename = image.FileId,
                        Data = image.ImageUrl,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_image",
                            ["responses.detail"] = image.Detail,
                            ["responses.file_id"] = image.FileId
                        }
                    };
                    break;
                case InputFilePart file:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(file.FileData ?? file.FileUrl),
                        Filename = file.Filename,
                        Data = file.FileData ?? file.FileUrl ?? file.FileId,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_file",
                            ["responses.file_id"] = file.FileId,
                            ["responses.file_url"] = file.FileUrl
                        }
                    };
                    break;
            }
        }
    }

    private static IEnumerable<ResponseContentPart> ToResponsesContentParts(IEnumerable<AIContentPart>? parts, string? role)
    {
        foreach (var part in parts ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    {
                        if (role == "assistant")
                        {
                            yield return new OutputTextPart
                            {
                                Text = text.Text,
                                Annotations = ExtractObject<object[]>(text.Metadata, "responses.annotations") ?? []
                            };
                        }
                        else
                        {
                            yield return new InputTextPart(text.Text);
                        }

                        break;
                    }

                case AIFileContentPart file:
                    {
                        if (role == "user")
                        {
                            if (file.MediaType?.StartsWith("image/") == true)
                            {
                                yield return new InputImagePart
                                {
                                    ImageUrl = file.Data?.ToString(),
                                };
                            }
                            else
                            {
                                var dataText = file.Data?.ToString();
                                yield return new InputFilePart
                                {
                                    Filename = file.Filename,
                                    FileId = ExtractValue<string>(file.Metadata, "responses.file_id"),
                                    FileData = dataText is not null && dataText.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? dataText : null,
                                    FileUrl = dataText is not null && dataText.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? dataText : ExtractValue<string>(file.Metadata, "responses.file_url")
                                };
                            }
                        }
                        break;
                    }
            }
        }
    }

    private static AIToolDefinition ToUnifiedTool(ResponseToolDefinition tool)
        => new()
        {
            Name = tool.Extra is not null && tool.Extra.TryGetValue("name", out var n) ? n.GetString() ?? tool.Type : tool.Type,
            Description = tool.Extra is not null && tool.Extra.TryGetValue("description", out var d) ? d.GetString() : null,
            InputSchema = tool.Extra is not null && tool.Extra.TryGetValue("parameters", out var p) ? p : null,
            Metadata = new Dictionary<string, object?>
            {
                ["responses.type"] = tool.Type,
                ["responses.extra"] = tool.Extra
            }
        };

    private static ResponseToolDefinition ToResponsesTool(AIToolDefinition tool)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(tool.Name, Json)
        };

        if (!string.IsNullOrWhiteSpace(tool.Description))
            extra["description"] = JsonSerializer.SerializeToElement(tool.Description, Json);

        if (tool.InputSchema is not null)
            extra["parameters"] = JsonSerializer.SerializeToElement(tool.InputSchema, Json);

        return new ResponseToolDefinition
        {
            Type = ExtractValue<string>(tool.Metadata, "responses.type") ?? "function",
            Extra = extra
        };
    }

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf(';');
            if (end > 5)
                return value[5..end];
        }

        return null;
    }
}
