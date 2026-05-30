namespace AIHappey.Core.Providers.Google;

public static class GoogleAIModels
{
    public static readonly Dictionary<string, DateTimeOffset> ModelCreatedAt = new()
    {
        // Gemini 2.5
        ["gemini-2.5-flash"] = DateTimeOffset.Parse("2025-06-17T00:00:00Z"),
        ["gemini-2.5-flash-preview-09-2025"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-2.5-flash-lite"] = DateTimeOffset.Parse("2025-07-22T00:00:00Z"),
        ["gemini-2.5-flash-lite-preview-09-2025"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-2.5-pro"] = DateTimeOffset.Parse("2025-06-17T00:00:00Z"),
        ["gemini-2.5-flash-image"] = DateTimeOffset.Parse("2025-10-02T00:00:00Z"),

        // Gemini 3
        ["gemini-3-pro-preview"] = DateTimeOffset.Parse("2025-11-18T00:00:00Z"),
        ["gemini-3-pro-image-preview"] = DateTimeOffset.Parse("2025-11-20T00:00:00Z"),
        ["gemini-3-flash-preview"] = DateTimeOffset.Parse("2025-12-17T00:00:00Z"),

        // Gemini 3.1
        ["gemini-3.1-pro-preview"] = DateTimeOffset.Parse("2026-02-19T00:00:00Z"),
        ["gemini-3.1-pro-preview-customtools"] = DateTimeOffset.Parse("2026-02-19T00:00:00Z"),
        ["gemini-3.1-flash-lite"] = DateTimeOffset.Parse("2026-05-07T00:00:00Z"),

        // Latest image GA models from Gemini API release notes
        ["gemini-3.1-flash-image"] = DateTimeOffset.Parse("2026-05-28T00:00:00Z"),
        ["gemini-3-pro-image"] = DateTimeOffset.Parse("2026-05-28T00:00:00Z"),

        // Gemini 3.5
        ["gemini-3.5-flash"] = DateTimeOffset.Parse("2026-05-19T00:00:00Z"),

        // Robotics
        ["gemini-robotics-er-1.5-preview"] = DateTimeOffset.Parse("2025-09-25T00:00:00Z"),
        ["gemini-robotics-er-1.6-preview"] = DateTimeOffset.Parse("2026-04-14T00:00:00Z"),

        // Managed Agents / agent endpoints
        ["antigravity-preview-05-2026"] = DateTimeOffset.Parse("2026-05-19T00:00:00Z"),
    };
}