using System.Text;
using AIHappey.Common.Model.Skills;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Orchestration;

public sealed class SkillProviderResolverTests
{
    [Fact]
    public async Task ResolveSkills_WhenStrictModeDisabled_ReturnsAllSkillProviders()
    {
        var resolver = CreateResolver(
            disableUnconfiguredSkillProviders: false,
            keys: new Dictionary<string, string?>(),
            new TestSkillProvider("configured", "configured-skill"),
            new TestSkillProvider("unconfigured", "unconfigured-skill"));

        var skills = await resolver.ResolveSkills();

        Assert.Contains(skills.Data ?? [], skill => skill.Id == "configured/configured-skill");
        Assert.Contains(skills.Data ?? [], skill => skill.Id == "unconfigured/unconfigured-skill");
    }

    [Fact]
    public async Task ResolveSkills_WhenStrictModeEnabled_ReturnsOnlyProvidersWithConfiguredKeys()
    {
        var resolver = CreateResolver(
            disableUnconfiguredSkillProviders: true,
            keys: new Dictionary<string, string?>
            {
                ["configured"] = "key"
            },
            new TestSkillProvider("configured", "configured-skill"),
            new TestSkillProvider("unconfigured", "unconfigured-skill"));

        var skills = await resolver.ResolveSkills();

        Assert.Contains(skills.Data ?? [], skill => skill.Id == "configured/configured-skill");
        Assert.DoesNotContain(skills.Data ?? [], skill => skill.Id == "unconfigured/unconfigured-skill");
    }

    [Fact]
    public async Task Resolve_WhenStrictModeEnabled_DoesNotBypassFilterForExplicitProviderId()
    {
        var resolver = CreateResolver(
            disableUnconfiguredSkillProviders: true,
            keys: new Dictionary<string, string?>(),
            new TestSkillProvider("unconfigured", "unconfigured-skill"));

        await Assert.ThrowsAsync<NotSupportedException>(() => resolver.RetrieveSkillContent("unconfigured/unconfigured-skill", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveSkills_WhenStrictModeEnabled_ReturnsProviderWithConfiguredSkillSource()
    {
        var resolver = CreateResolver(
            disableUnconfiguredSkillProviders: true,
            keys: new Dictionary<string, string?>(),
            new TestSkillProvider("azure", "storage-skill", hasConfiguredSkillSource: true),
            new TestSkillProvider("unconfigured", "unconfigured-skill"));

        var skills = await resolver.ResolveSkills();

        Assert.Contains(skills.Data ?? [], skill => skill.Id == "azure/storage-skill");
        Assert.DoesNotContain(skills.Data ?? [], skill => skill.Id == "unconfigured/unconfigured-skill");
    }

    private static SkillProviderResolver CreateResolver(
        bool disableUnconfiguredSkillProviders,
        IReadOnlyDictionary<string, string?> keys,
        params ISkillProvider[] providers)
        => new(
            new TestApiKeyResolver(keys),
            providers,
            Options.Create(new SkillProviderResolverOptions
            {
                DisableUnconfiguredSkillProviders = disableUnconfiguredSkillProviders
            }));

    private sealed class TestApiKeyResolver(IReadOnlyDictionary<string, string?> keys) : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => keys.TryGetValue(provider, out var key) ? key : null;
    }

    private sealed class TestSkillProvider(
        string identifier,
        string skillId,
        bool hasConfiguredSkillSource = false) : ISkillProvider, IConfiguredSkillProvider
    {
        public bool HasConfiguredSkillSource { get; } = hasConfiguredSkillSource;

        public string GetIdentifier() => identifier;

        public Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Skill>>(
            [
                new Skill
                {
                    Id = skillId,
                    Name = skillId,
                    CreatedAt = 1,
                    DefaultVersion = "1.0.0",
                    LatestVersion = "1.0.0"
                }
            ]);

        public Task<IEnumerable<SkillVersion>> ListSkillVersions(string skillId, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<SkillVersion>>(
            [
                new SkillVersion
                {
                    Id = $"{skillId}:1.0.0",
                    SkillId = skillId,
                    Name = skillId,
                    Version = "1.0.0",
                    CreatedAt = 1
                }
            ]);

        public Task<Stream> RetrieveSkillContent(string skillId, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes($"content:{skillId}")));

        public Task<Stream> RetrieveSkillVersionContent(string skillId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes($"content:{skillId}:{version}")));
    }
}
