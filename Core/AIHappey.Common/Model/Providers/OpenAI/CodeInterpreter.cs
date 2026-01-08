using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class CodeInterpreter
{
    [JsonPropertyName("container")]
    [JsonConverter(typeof(ContainerUnionConverter))]
    public ContainerUnion? Container { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}

