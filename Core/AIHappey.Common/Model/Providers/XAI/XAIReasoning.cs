using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIReasoning
{

    //[JsonPropertyName("effort")]
    //public string? Effort { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

}

