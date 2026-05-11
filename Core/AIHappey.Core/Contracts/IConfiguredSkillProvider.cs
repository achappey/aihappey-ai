namespace AIHappey.Core.Contracts;

/// <summary>
/// Optional extension for skill providers that can be used without a provider API key
/// when provider-specific skill storage/configuration is present.
/// </summary>
public interface IConfiguredSkillProvider
{
    bool HasConfiguredSkillSource { get; }
}
