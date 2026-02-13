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

        var start = DateTime.UtcNow;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var current = await poll(cancellationToken).ConfigureAwait(false);
            if (isTerminal(current))
                return current;

            if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
                throw new TimeoutException($"Polling exceeded max attempts ({maxAttempts}).");

            if (timeout.HasValue && DateTime.UtcNow - start >= timeout.Value)
                throw new TimeoutException($"Polling exceeded timeout ({timeout}).");

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}

