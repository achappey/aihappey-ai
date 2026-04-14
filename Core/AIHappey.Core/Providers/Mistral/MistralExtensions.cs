using System.Text.Json;

namespace AIHappey.Core.Providers.Mistral;

public static partial class MistralExtensions
{

    public static double? GetDouble(
    this Dictionary<string, object>? dict,
    string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,

            string s when double.TryParse(s, out var d) => d,

            JsonElement j when j.ValueKind == JsonValueKind.Number && j.TryGetDouble(out var d) => d,

            _ => null
        };
    }
}
