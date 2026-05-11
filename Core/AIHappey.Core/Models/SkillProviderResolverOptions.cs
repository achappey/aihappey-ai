namespace AIHappey.Core.Models;

public sealed class SkillProviderResolverOptions
{
    /// <summary>
    /// When enabled, only skill providers with an explicitly configured API key
    /// or a configured provider-specific skill source are exposed.
    /// </summary>
    public bool DisableUnconfiguredSkillProviders { get; set; } = false;
}
