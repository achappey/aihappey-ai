using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqCodeInterpreter
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "code_interpreter";

    [JsonPropertyName("container")]
    public GroqCodeInterpreterContainer? Container { get; set; } = new();
}

