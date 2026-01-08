using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIXCodeExecution
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}

