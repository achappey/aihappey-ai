using AIHappey.Vercel.Models;

namespace AIHappey.Tests.TestInfrastructure;

internal static class FixtureAssertions
{
    public static void AssertContainsSubsequence(IReadOnlyList<string> actual, params string[] expected)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);

        var actualIndex = 0;

        foreach (var expectedItem in expected)
        {
            var found = false;

            while (actualIndex < actual.Count)
            {
                if (string.Equals(actual[actualIndex], expectedItem, StringComparison.Ordinal))
                {
                    found = true;
                    actualIndex++;
                    break;
                }

                actualIndex++;
            }

            Assert.True(
                found,
                $"Expected ordered event '{expectedItem}' in sequence [{string.Join(", ", actual)}].");
        }
    }

    public static async Task<List<T>> CollectAsync<T>(
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var results = new List<T>();

        await foreach (var item in source.WithCancellation(cancellationToken))
            results.Add(item);

        return results;
    }

    public static void AssertAllSourceUrlsAreValid(IEnumerable<UIMessagePart> uiParts)
    {
        ArgumentNullException.ThrowIfNull(uiParts);

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();

        Assert.All(
            sourceParts,
            part => Assert.True(
                Uri.TryCreate(part.Url, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri,
                $"Expected source-url UI part url to be a valid absolute URI, but found '{part.Url}'."));
    }
}
