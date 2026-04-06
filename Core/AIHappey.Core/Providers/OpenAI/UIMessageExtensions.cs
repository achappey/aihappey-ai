using OpenAI.Responses;
using System.ClientModel.Primitives;

namespace AIHappey.Core.Providers.OpenAI;

public static class UIMessageExtensions
{
    public static ResponseTool CreateCustomTool(this string type, IDictionary<string, object?>? extra = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = type
        };

        if (extra != null)
            foreach (var kv in extra) dict[kv.Key] = kv.Value;

        // Important: use "J" (JSON) format so the SDK uses its IJsonModel path.
        return ModelReaderWriter.Read<ResponseTool>(
            BinaryData.FromObjectAsJson(dict),
            new ModelReaderWriterOptions("J")
        )!;
    }
}
