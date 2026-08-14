using System.Reflection;
using AIHappey.Core.Providers.Azure;

namespace AIHappey.Tests.Azure;

public sealed class AzureProviderSkillsTests
{
    [Theory]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("  LaTeSt  ")]
    public void LatestVersionAlias_IsTrimmedAndCaseInsensitive(string version)
        => Assert.True(IsLatestVersionAlias(version));

    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("latest-preview")]
    public void ExplicitVersion_IsNotTreatedAsLatestAlias(string version)
        => Assert.False(IsLatestVersionAlias(version));

    [Theory]
    [InlineData("1.9", "1.10", -1)]
    [InlineData("2", "1.99", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    public void VersionOrdering_UsesNumericComponents(string left, string right, int expectedSign)
    {
        var comparison = InvokePrivateStatic<int>("CompareVersionNumbers", left, right);

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    private static bool IsLatestVersionAlias(string version)
        => InvokePrivateStatic<bool>("IsLatestVersionAlias", version);

    private static TResult InvokePrivateStatic<TResult>(string methodName, params object?[] arguments)
    {
        var method = typeof(AzureProvider).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Could not find AzureProvider.{methodName}.");

        return (TResult)(method.Invoke(null, arguments)
            ?? throw new InvalidOperationException($"AzureProvider.{methodName} returned null."));
    }
}
