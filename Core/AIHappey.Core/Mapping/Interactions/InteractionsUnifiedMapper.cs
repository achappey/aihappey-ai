using System.Text.Json;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = InteractionJson.Default;
}
