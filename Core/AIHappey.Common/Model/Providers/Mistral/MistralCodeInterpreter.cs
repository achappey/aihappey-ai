using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralCodeInterpreter
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}

