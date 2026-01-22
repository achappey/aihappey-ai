namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void EnsureIsRawBase64Only(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{fieldName} is required.");

        var s = value.TrimStart();
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fieldName} must be raw base64 (data URLs are not supported).");
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fieldName} must be raw base64 (URLs are not supported and will not be downloaded).");
    }
}

