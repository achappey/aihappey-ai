namespace AIHappey.Abstractions.Http;

public sealed class ProviderBackendCaptureRequest
{
    public bool? Enabled { get; init; }

    public string? RelativeDirectory { get; init; }

    public string? FileName { get; init; }

    public static ProviderBackendCaptureRequest Disabled()
        => new()
        {
            Enabled = false
        };

    public static ProviderBackendCaptureRequest Create(string relativeDirectory, string? fileName = null)
        => new()
        {
            RelativeDirectory = relativeDirectory,
            FileName = fileName
        };
}
