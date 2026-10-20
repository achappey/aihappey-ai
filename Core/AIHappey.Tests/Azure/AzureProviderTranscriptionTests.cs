using System.Reflection;
using AIHappey.Core.Providers.Azure;

namespace AIHappey.Tests.Azure;

public sealed class AzureProviderTranscriptionTests
{
    [Fact]
    public void CombineTranscriptionTexts_joinsRecognizedUtterancesAndIgnoresBlankText()
    {
        var method = typeof(AzureProvider).GetMethod(
            "CombineTranscriptionTexts",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find AzureProvider.CombineTranscriptionTexts.");

        var text = (string?)method.Invoke(null, [new[] { " First utterance. ", "", "Second utterance." }]);

        Assert.Equal("First utterance. Second utterance.", text);
    }
}
