using System.Reflection;
using AIHappey.Core.Providers.Google;

namespace AIHappey.Tests.Google;

public sealed class GoogleModelDiscoveryTests
{
    [Theory]
    [InlineData("models/imagen-4.0-generate-001", true)]
    [InlineData("imagen-3.0-generate-002", true)]
    [InlineData("models/gemini-3.1-flash-image", false)]
    [InlineData(null, false)]
    public void IsImagenModelRecognizesModelIdsInGoogleResourceNames(string? name, bool expected)
    {
        var method = typeof(GoogleAIProvider).GetMethod("IsImagenModel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "IsImagenModel");

        Assert.Equal(expected, method.Invoke(null, [name]));
    }
}
