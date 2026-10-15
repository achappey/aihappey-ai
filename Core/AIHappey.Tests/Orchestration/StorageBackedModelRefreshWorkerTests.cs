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

    [Fact]
    public async Task Failed_queue_receive_is_logged_without_faulting_the_worker()
    {
        var queue = new FailingReceiveQueue();
        var logger = new RecordingLogger<StorageBackedModelRefreshWorker>();
        var worker = new StorageBackedModelRefreshWorker(
            new FailingScopeFactory(),
            queue,
            Options.Create(new ModelListingStorageOptions { MaxParallelBackgroundRefresh = 1 }),
            logger);

        await worker.StartAsync(CancellationToken.None);

        var loggedException = await logger.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(loggedException);
        Assert.False(worker.ExecuteTask!.IsFaulted);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Waits_for_capacity_before_receiving_another_message()
    {
        var queue = new CountingQueue();
        var scopeFactory = new BlockingScopeFactory();
        var worker = new StorageBackedModelRefreshWorker(
            scopeFactory,
            queue,
            Options.Create(new ModelListingStorageOptions { MaxParallelBackgroundRefresh = 1 }),
            new RecordingLogger<StorageBackedModelRefreshWorker>());

        await worker.StartAsync(CancellationToken.None);
        await scopeFactory.ServiceResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var receiveAttempt = await Task.WhenAny(
            queue.SecondReceiveAttempted.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(queue.SecondReceiveAttempted.Task, receiveAttempt);

        scopeFactory.ReleaseServiceResolution.TrySetResult();
        await queue.SecondReceiveAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);
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

    private sealed class FailingReceiveQueue : IModelListingRefreshQueue
    {
        public bool IsEnabled => true;

        public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromException<ModelListingQueueMessage?>(new InvalidOperationException("Queue unavailable."));

        public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class CountingQueue : IModelListingRefreshQueue
    {
        private int _receivedMessages;

        public bool IsEnabled => true;

        public TaskCompletionSource SecondReceiveAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _receivedMessages) == 1)
            {
                return Task.FromResult<ModelListingQueueMessage?>(new ModelListingQueueMessage
                {
                    MessageId = "message",
                    PopReceipt = "receipt",
                    Request = new ModelListingRefreshRequest
                    {
                        ProviderId = "provider",
                        CacheKey = "models:provider"
                    }
                });
            }

            SecondReceiveAttempted.TrySetResult();
            return Task.FromResult<ModelListingQueueMessage?>(null);
        }

        public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

    private sealed class BlockingScopeFactory : IServiceScopeFactory
    {
        public TaskCompletionSource ServiceResolutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseServiceResolution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope() => new BlockingScope(this);

        private sealed class BlockingScope(BlockingScopeFactory factory) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                factory.ServiceResolutionStarted.TrySetResult();
                factory.ReleaseServiceResolution.Task.GetAwaiter().GetResult();
                return null;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
