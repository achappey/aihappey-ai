namespace AIHappey.Core.Orchestration;

/// <summary>
/// Indicates that model resolution completed without finding a provider for the requested model.
/// </summary>
public sealed class ModelProviderNotFoundException(string model)
    : NotSupportedException($"No provider found for model '{model}'.")
{
    public string Model { get; } = model;
}
