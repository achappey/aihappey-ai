namespace AIHappey.Core.Extensions;

internal static class RealtimeCapabilities
{
    internal static bool IsOpenAIRealtime(string model)
        => model.StartsWith("gpt-realtime", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("gpt-4o-realtime", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("gpt-4o-mini-realtime", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpenAILive(string model)
        => model.StartsWith("gpt-live-", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpenAIRealtimeSpeech(string model)
        => System.Text.RegularExpressions.Regex.IsMatch(model,
            @"^gpt-realtime-2\.1-mini(?:-\d{4}-\d{2}-\d{2})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsGoogleLive(string model)
        => model.Contains("-live", StringComparison.OrdinalIgnoreCase)
           || model.Contains("native-audio", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSpaceXAIRealtime(string model)
        => model.Equals("grok-voice-latest", StringComparison.OrdinalIgnoreCase)
           || model.Equals("grok-voice", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("grok-voice-think-fast-", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string>? Tags(string provider, string model, IEnumerable<string>? existing)
    {
        var tags = new HashSet<string>(existing ?? [], StringComparer.Ordinal);
        var token = provider switch
        {
            "openai" => IsOpenAIRealtime(model),
            "google" => IsGoogleLive(model),
            "spacexai" => IsSpaceXAIRealtime(model),
            _ => false
        };
        var session = provider == "openai" && (IsOpenAIRealtime(model) || IsOpenAILive(model));
        if (token || session) tags.Add("real-time");
        if (token) tags.Add("realtime-token");
        if (session) tags.Add("realtime-session");
        if (provider == "openai" && IsOpenAIRealtimeSpeech(model)) tags.Add("realtime-speech");
        return tags.Count == 0 ? null : tags.ToArray();
    }
}
