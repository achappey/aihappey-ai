namespace AIHappey.Abstractions.Http;

public sealed class ProviderBackendCaptureOptions
{
    public bool Enabled { get; set; }

    public bool DevelopmentOnly { get; set; } = true;

    public string RootDirectory { get; set; } = Path.Combine("captures", "provider-raw");
}
