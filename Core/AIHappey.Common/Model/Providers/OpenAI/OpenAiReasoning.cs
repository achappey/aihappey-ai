namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiReasoning
{
    // public ResponseReasoningEffortLevel? Effort { get; set; }    // none, minimal, low, medium, high
    public string? Effort { get; set; } // none, minimal, low, medium, high
    public string? Summary { get; set; } // auto, concise, detailed
}

