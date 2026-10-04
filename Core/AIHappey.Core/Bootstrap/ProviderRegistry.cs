using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIHappey.Core.AI;

/// <summary>
/// Shared registration metadata only. Provider instances are always obtained from the caller's scope.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<string, Type> _modelProviders;
    private readonly IReadOnlyDictionary<string, Type> _skillProviders;

    public ProviderRegistry(
        IReadOnlyDictionary<string, Type> modelProviders,
        IReadOnlyDictionary<string, Type> skillProviders)
    {
        _modelProviders = new Dictionary<string, Type>(modelProviders, StringComparer.OrdinalIgnoreCase);
        _skillProviders = new Dictionary<string, Type>(skillProviders, StringComparer.OrdinalIgnoreCase);
        ModelProviderIds = Array.AsReadOnly(_modelProviders.Keys.ToArray());
        SkillProviderIds = Array.AsReadOnly(_skillProviders.Keys.ToArray());
    }

    public IReadOnlyList<string> ModelProviderIds { get; }
    public IReadOnlyList<string> SkillProviderIds { get; }

    public bool HasModelProvider(string identifier) => _modelProviders.ContainsKey(identifier);

    public bool HasSkillProvider(string identifier) => _skillProviders.ContainsKey(identifier);

    public bool HasConfigurableSkillSource(string identifier)
        => _skillProviders.TryGetValue(identifier, out var type)
            && typeof(IConfiguredSkillProvider).IsAssignableFrom(type);

    public IModelProvider? GetModelProvider(IServiceProvider services, string identifier)
        => HasModelProvider(identifier)
            ? services.GetRequiredKeyedService<IModelProvider>(identifier.ToLowerInvariant())
            : null;

    public ISkillProvider? GetSkillProvider(IServiceProvider services, string identifier)
        => HasSkillProvider(identifier)
            ? services.GetRequiredKeyedService<ISkillProvider>(identifier.ToLowerInvariant())
            : null;

    internal static void Register(IServiceCollection services)
    {
        var models = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        // Preserve enumeration order and compatibility, but make every alias (including keyed
        // aliases and repeated registrations) resolve the same canonical concrete service.
        foreach (var descriptor in services.Where(d => !d.IsKeyedService
                     && (d.ServiceType == typeof(IModelProvider) || d.ServiceType == typeof(ISkillProvider))).ToArray())
        {
            var type = descriptor.ImplementationType
                ?? throw new InvalidOperationException("Provider registrations must expose their implementation type.");
            var identifier = GetIdentifier(type);
            var registrations = descriptor.ServiceType == typeof(IModelProvider) ? models : skills;
            if (registrations.TryGetValue(identifier, out var existing) && existing != type)
                throw new InvalidOperationException($"Duplicate provider identifier '{identifier}'.");

            services.TryAdd(ServiceDescriptor.Describe(type, type, descriptor.Lifetime));
            services[services.IndexOf(descriptor)] = ServiceDescriptor.Describe(
                descriptor.ServiceType, sp => sp.GetRequiredService(type), descriptor.Lifetime);

            if (registrations.TryAdd(identifier, type))
                services.Add(new ServiceDescriptor(descriptor.ServiceType, identifier,
                    (sp, _) => sp.GetRequiredService(type), descriptor.Lifetime));
        }

        services.AddSingleton(new ProviderRegistry(models, skills));
    }

    private static string GetIdentifier(Type type)
        => type == typeof(GoogleAIProvider)
            ? "google"
            : type.Name.EndsWith("Provider", StringComparison.Ordinal)
                ? type.Name[..^"Provider".Length].ToLowerInvariant()
                : throw new InvalidOperationException($"Provider type '{type}' has no identifier convention.");
}
