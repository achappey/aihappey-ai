namespace AIHappey.Core.Providers.Cohere;

public static partial class CohereExtensions
{
    public const string CohereIdentifier = "cohere";

    public static string ToFinishReason(this string? finishReason) =>
        finishReason?.ToLowerInvariant() switch
        {
            null => "stop",
            "complete" => "stop",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "tool_call" => "tool-calls",
            "error" => "error",
            "timeout" => "error",
            _ => "stop"
        };


}
