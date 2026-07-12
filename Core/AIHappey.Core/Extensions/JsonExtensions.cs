using System.Text.Json;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Extensions;

public static class JsonExtensions
{
    public static string? TryGetId(this JsonElement element)
        => element.TryGetString("id") ?? null;
}
