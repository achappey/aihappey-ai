
namespace AIHappey.Core.Providers.Google;

public static class GoogleAIModels
{

    public static readonly Dictionary<string, DateTimeOffset> ModelCreatedAt = new()
    {
        ["gemini-2.5-flash"] = DateTimeOffset.Parse("2025-06-17T00:00:00Z"),
        ["gemini-2.5-flash-preview-09-2025"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-2.5-flash-lite"] = DateTimeOffset.Parse("2025-07-22T00:00:00Z"),
        ["gemini-2.5-flash-lite-preview-09-2025"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-2.5-pro"] = DateTimeOffset.Parse("2025-06-17T00:00:00Z"),
        ["gemini-robotics-er-1.5-preview"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-2.5-flash-image"] = DateTimeOffset.Parse("2025-10-02T00:00:00Z"),
        ["gemini-3-pro-preview"] = DateTimeOffset.Parse("2025-11-18T00:00:00Z"),
        ["gemini-3-pro-image-preview"] = DateTimeOffset.Parse("2025-11-20T00:00:00Z"),
    };
}
