using System.Text.Json;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
}
