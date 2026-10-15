namespace AIHappey.Core.AI;

public static class AsyncTaskPollingExtensions
{
    /// <summary>
    /// Generic polling helper for async task APIs that return a status.
    /// </summary>
    /// <typeparam name="T">Status payload type</typeparam>
    /// <param name="poll">Poll function. Should return current status payload.</param>
    /// <param name="isTerminal">Returns true when the payload is a terminal state (COMPLETED or FAILED).</param>
    /// <param name="interval">Delay between polls.</param>
    /// <param name="timeout">Overall timeout. If null, no timeout is enforced.</param>
    /// <param name="maxAttempts">Optional max attempts. If null, attempts are unbounded (bounded only by timeout).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<T> PollUntilTerminalAsync<T>(
        Func<CancellationToken, Task<T>> poll,
        Func<T, bool> isTerminal,
        TimeSpan interval,
        TimeSpan? timeout,
        int? maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCancellation = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : null;
        using var pollingCancellation = timeoutCancellation is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token)
            : null;
        var pollingToken = pollingCancellation?.Token ?? cancellationToken;
        var attempt = 0;

        try
        {
            while (true)
            {
                pollingToken.ThrowIfCancellationRequested();
                attempt++;

                var current = await poll(pollingToken);
                if (timeoutCancellation?.IsCancellationRequested == true)
                    throw new OperationCanceledException(pollingToken);
                if (isTerminal(current))
                    return current;

                if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
                    throw new TimeoutException($"Polling exceeded max attempts ({maxAttempts}).");

                await Task.Delay(interval, pollingToken);
            }
        }
        catch (OperationCanceledException) when (
            timeoutCancellation?.IsCancellationRequested == true
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Polling exceeded timeout ({timeout}).");
        }

    }
}
