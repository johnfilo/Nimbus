using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Nimbus.Configuration.Settings;
using Nimbus.Extensions;

namespace Nimbus.Infrastructure.MessageSendersAndReceivers
{
    internal abstract class NimbusMessageReceiver : INimbusMessageReceiver
    {
        protected readonly ConcurrentHandlerLimitSetting ConcurrentHandlerLimit;
        private readonly ILogger _logger;
        private bool _running;

        private readonly object _mutex = new object();
        private Task _workerTask;
        private readonly SemaphoreSlim _throttle;
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();

        protected NimbusMessageReceiver(ConcurrentHandlerLimitSetting concurrentHandlerLimit, ILogger logger)
        {
            ConcurrentHandlerLimit = concurrentHandlerLimit;
            _logger = logger;
            _throttle = new SemaphoreSlim(concurrentHandlerLimit, concurrentHandlerLimit);
        }

        public async Task Start(Func<BrokeredMessage, Task> callback)
        {
            if (_running) throw new InvalidOperationException("Already started!");
            _running = true;

            await CreateBatchReceiver();
            _workerTask = Task.Run(() => Worker(callback));
        }

        protected abstract Task CreateBatchReceiver();
        protected abstract Task<BrokeredMessage[]> FetchBatch(int batchSize);
        protected abstract void StopBatchReceiver();

        private async Task Worker(Func<BrokeredMessage, Task> callback)
        {
            while (_running)
            {
                try
                {
                    var messages = await FetchBatchInternal(_throttle.CurrentCount);
                    if (!_running) return;
                    if (messages.None()) continue;

                    var tasks = messages
                        .Select(async m =>
                                      {
                                          try
                                          {
                                              await _throttle.WaitAsync(_cancellationToken.Token);
                                              await callback(m);
                                          }
                                          finally
                                          {
                                              _throttle.Release();
                                          }
                                      })
                        .ToArray();

                    await Task.WhenAny(tasks);
                }
                catch (OperationCanceledException)
                {
                    // will be thrown when someone calls .Stop() on us
                }
                catch (Exception exc)
                {
                    _logger.Error(exc, "Worker exception in {0} for {1}", GetType().Name, this);
                }
            }
        }

        private async Task<BrokeredMessage[]> FetchBatchInternal(int currentCount)
        {
            var fetchTask = FetchBatch(currentCount);
            var cancelTask = Task.Run(async () =>
                                            {
                                                while (!_cancellationToken.IsCancellationRequested)
                                                {
                                                    await Task.Delay(100);
                                                }
                                            });

            await Task.WhenAny(fetchTask, cancelTask);

            return fetchTask.IsCompleted
                ? fetchTask.Result
                : new BrokeredMessage[0];
        }

        public async Task Stop()
        {
            if (!_running) return;
            _running = false;
            _cancellationToken.Cancel();

            StopBatchReceiver();
            await _workerTask;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            Stop().Wait();
            _throttle.Dispose();
        }
    }
}