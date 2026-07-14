
namespace AIHappey.Core.AI;

public static class VoiceExtensions
{
    private static readonly IReadOnlyDictionary<string, string> LanguageAliases =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Language names
        ["Arabic"] = "ar",
        ["Chinese"] = "zh",
        ["English"] = "en",
        ["French"] = "fr",
        ["German"] = "de",
        ["Hindi"] = "hi",
        ["Indonesian"] = "id",
        ["Italian"] = "it",
        ["Polish"] = "pl",
        ["Portuguese"] = "pt",
        ["Romanian"] = "ro",
        ["Spanish"] = "es",
        ["Thai"] = "th",
        ["Turkish"] = "tr",
        ["Turkey"] = "tr",
        ["Vietnamese"] = "vi",

        // ISO 639-2 / ISO 639-3 codes
        ["ara"] = "ar",
        ["eng"] = "en",

        ["fra"] = "fr",
        ["fre"] = "fr",

        ["ger"] = "de",
        ["deu"] = "de",

        ["heb"] = "he",
        ["hin"] = "hi",
        ["jpn"] = "ja",

        ["por"] = "pt",
        ["spa"] = "es",

        // Chinese language variants grouped under Chinese
        ["cmn"] = "zh", // Mandarin
        ["yue"] = "zh", // Cantonese
        ["wuu"] = "zh", // Wu Chinese

        // Filipino has no exact ISO 639-1 equivalent.
        // For filtering, group Filipino and Tagalog under "tl".
        ["fil"] = "tl"
    };

    public static string NormalizeLanguageCode(this string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        var value = code.Trim();

        // First try the complete value:
        // "German" -> "de"
        // "Portuguese" -> "pt"
        if (LanguageAliases.TryGetValue(value, out var mapped))
            return mapped;

        // Only inspect the primary language portion:
        // "de-CH" -> "de"
        // "cmn-CN" -> "cmn"
        // "zh_CN" -> "zh"
        var primaryCode = value
            .Replace('_', '-')
            .Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(primaryCode))
            return code;

        // Map three-letter codes and aliases:
        // "cmn-CN" -> "zh"
        // "eng-US" -> "en"
        if (LanguageAliases.TryGetValue(primaryCode, out mapped))
            return mapped;

        // Already a proper two-letter language code.
        if (primaryCode.Length == 2 &&
            primaryCode.All(char.IsLetter))
        {
            return primaryCode.ToLowerInvariant();
        }

        // Not recognized as a language.
        // Keep values such as "female", "voice", "tts" and "unknown" unchanged.
        return code;
    }

}
