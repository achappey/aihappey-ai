using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
}
