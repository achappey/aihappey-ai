
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class OpenAiProviderMetadata
{
    public OpenAiReasoning? Reasoning { get; set; }

    [JsonPropertyName("web_search")]
    public OpenAiWebSearch? WebSearch { get; set; }

    [JsonPropertyName("file_search")]
    public OpenAiFileSearch? FileSearch { get; set; }

    [JsonPropertyName("code_interpreter")]
    public CodeInterpreter? CodeInterpreter { get; set; }

    [JsonPropertyName("image_generation")]
    public ImageGeneration? ImageGeneration { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("max_tool_calls")]
    public int? MaxToolCalls { get; set; }

    [JsonPropertyName("mcp_list_tools")]
    public IEnumerable<OpenAiMcpTool>? MCPTools { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("include")]
    public IEnumerable<string>? Include { get; set; }
}


public class ImageGeneration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_generation";

    [JsonPropertyName("background")]
    public string? Background { get; set; } = "auto";
    // transparent | opaque | auto

    [JsonPropertyName("input_fidelity")]
    public string? InputFidelity { get; set; } = "low";
    // high | low

    [JsonPropertyName("input_image_mask")]
    public InputImageMask? InputImageMask { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; } = "gpt-image-1.5";

    [JsonPropertyName("moderation")]
    public string? Moderation { get; set; } = "auto";

    [JsonPropertyName("output_compression")]
    public int? OutputCompression { get; set; } = 100;
    // 0–100

    [JsonPropertyName("partial_images")]
    public int? PartialImages { get; set; } = 0;
    // 0–3

    [JsonPropertyName("quality")]
    public string? Quality { get; set; } = "auto";
    // low | medium | high | auto

    [JsonPropertyName("size")]
    public string? Size { get; set; } = "auto";
    // 1024x1024 | 1024x1536 | 1536x1024 | auto
}

public class InputImageMask
{
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }
}


public class OpenAiMcpTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "mcp";

    [JsonPropertyName("server_label")]
    public string ServerLabel { get; set; } = default!;

    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; set; }

    [JsonPropertyName("connector_id")]
    public string? ConnectorId { get; set; }

    [JsonPropertyName("require_approval")]
    public string RequireApproval { get; set; } = "never";

    [JsonPropertyName("allowed_tools")]
    public List<string> AllowedTools { get; set; } = [];

    [JsonPropertyName("authorization")]
    public string? Authorization { get; set; }
}

public class CodeInterpreter
{
    [JsonPropertyName("container")]
    [JsonConverter(typeof(ContainerUnionConverter))]
    public ContainerUnion? Container { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}

/// Holds either a string or CodeInterpreterContainer
public readonly struct ContainerUnion
{
    public string? String { get; }
    public CodeInterpreterContainer? Object { get; }

    public bool IsString => String is not null;
    public bool IsObject => Object is not null;

    public ContainerUnion(string value) { String = value; Object = null; }
    public ContainerUnion(CodeInterpreterContainer value) { Object = value; String = null; }

    public static implicit operator ContainerUnion(string value) => new(value);
    public static implicit operator ContainerUnion(CodeInterpreterContainer value) => new(value);
}

sealed class ContainerUnionConverter : JsonConverter<ContainerUnion>
{
    public override ContainerUnion Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return default;

        if (reader.TokenType == JsonTokenType.String)
            return new ContainerUnion(reader.GetString() ?? string.Empty);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var obj = JsonSerializer.Deserialize<CodeInterpreterContainer>(ref reader, options)
                      ?? new CodeInterpreterContainer();
            return new ContainerUnion(obj);
        }

        throw new JsonException("Expected string or object for 'container'.");
    }

    public override void Write(Utf8JsonWriter writer, ContainerUnion value, JsonSerializerOptions options)
    {
        if (value.IsString)
        {
            writer.WriteStringValue(value.String);
        }
        else if (value.IsObject)
        {
            JsonSerializer.Serialize(writer, value.Object, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public class CodeInterpreterContainer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class OpenAiReasoning
{
    //public ResponseReasoningEffortLevel? Effort { get; set; }    // none, minimal, low, medium, high
    public string? Effort { get; set; }    // none, minimal, low, medium, high
    public string? Summary { get; set; }   // auto, concise, detailed
}

public class OpenAiWebSearch
{
    [JsonPropertyName("search_context_size")]
    public string? SearchContextSize { get; set; } // medium, high, low, etc.

    [JsonPropertyName("user_location")]
    public OpenAiUserLocation? UserLocation { get; set; }
}

public class OpenAiFileSearch
{
    [JsonPropertyName("vector_store_ids")]
    public List<string>? VectorStoreIds { get; set; }

    [JsonPropertyName("max_num_results")]
    public int? MaxNumResults { get; set; }
}

public class OpenAiUserLocation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "approximate";

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}