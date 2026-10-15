using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Orchestration;

public sealed class StorageBackedModelRefreshWorkerTests
{
    [Fact]
    public async Task Failed_refresh_is_logged_and_message_is_left_for_retry()
    {
        var queue = new SingleMessageQueue();
        var logger = new RecordingLogger<StorageBackedModelRefreshWorker>();
        var worker = new StorageBackedModelRefreshWorker(
            new FailingScopeFactory(),
            queue,
            Options.Create(new ModelListingStorageOptions { MaxParallelBackgroundRefresh = 1 }),
            logger);

        await worker.StartAsync(CancellationToken.None);

        var loggedException = await logger.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.IsType<InvalidOperationException>(loggedException);
        Assert.Equal(0, queue.DeletedMessages);
    }

    private sealed class SingleMessageQueue : IModelListingRefreshQueue
    {
        private readonly ModelListingQueueMessage _message = new()
        {
            MessageId = "message",
            PopReceipt = "receipt",
            Request = new ModelListingRefreshRequest
            {
                ProviderId = "provider",
                CacheKey = "models:provider"
            }
        };

        private int _receivedMessages;

        public bool IsEnabled => true;

        public int DeletedMessages { get; private set; }

        public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ModelListingQueueMessage?>(
                Interlocked.Increment(ref _receivedMessages) == 1 ? _message : null);

        public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
        {
            DeletedMessages++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FailingScope();
    }

    private sealed class FailingScope : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource<Exception?> ErrorLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                ErrorLogged.TrySetResult(exception);
        }
    }
}
