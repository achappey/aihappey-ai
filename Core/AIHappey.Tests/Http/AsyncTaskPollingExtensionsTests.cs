namespace AIHappey.Tests.Http;

public sealed class AsyncTaskPollingExtensionsTests
{
    [Fact]
    public async Task PollUntilTerminalAsync_TimesOutWhilePollIsInFlight()
    {
        var pollStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Poll(CancellationToken cancellationToken)
        {
            pollStarted.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }

        var polling = AIHappey.Core.AI.AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            Poll,
            _ => false,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(100),
            maxAttempts: null);

        var observedToken = await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() => polling);
        Assert.True(observedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PollUntilTerminalAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var pollStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Poll(CancellationToken cancellationToken)
        {
            pollStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }

        var polling = AIHappey.Core.AI.AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            Poll,
            _ => false,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(5),
            maxAttempts: null,
            cancellation.Token);

        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
    }
}
