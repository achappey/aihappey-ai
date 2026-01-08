using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class CodeInterpreterContainer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

