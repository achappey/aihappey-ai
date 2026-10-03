namespace AIHappey.Tests.TestInfrastructure;

// Capture configuration is process-wide. Tests that change it must not overlap
// any other collection, including providers that capture without explicit metadata.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackendCaptureCollection
{
    public const string Name = "Global backend capture configuration";
}
