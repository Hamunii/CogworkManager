using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Cogwork.Core;

/// <summary>
/// Performs async tasks deduplicating executing tasks by id.
/// </summary>
public class TaskManager
{
    public static TaskManager IconQueue { get; } = new(20);

    readonly Channel<(
        string id,
        Func<CancellationToken, Task> func,
        CancellationTokenSource cts
    )> _queue;
    readonly Task[] _workers;

    readonly ConcurrentDictionary<
        string,
        (Func<CancellationToken, Task> func, CancellationTokenSource cts)
    > idToLatest = [];
    readonly ConcurrentDictionary<string, bool> idIsRemoved = [];

    public TaskManager(int capacity)
    {
        BoundedChannelOptions options = new(capacity) { FullMode = BoundedChannelFullMode.Wait };
        _queue = Channel.CreateBounded<(
            string,
            Func<CancellationToken, Task>,
            CancellationTokenSource
        )>(options);

        _workers = new Task[capacity];
        for (int i = 0; i < capacity; i++)
        {
            _workers[i] = Task.Run(ConsumeQueueAsync);
        }
    }

    public void Enqueue(
        string id,
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken
    )
    {
        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var value = (job, tokenSource);
        _ = idIsRemoved.TryRemove(id, out _);

        idToLatest.AddOrUpdate(
            id,
            addValueFactory: (id) =>
            {
                _ = Task.Run(() => _queue.Writer.WriteAsync((id, job, tokenSource)));
                Cog.Information($"Adding new with id '{id}'");
                return value;
            },
            updateValueFactory: (id, old) =>
            {
                Cog.Information($"Updating with id '{id}'");
                return value;
            }
        );
    }

    public void Remove(string id)
    {
        idIsRemoved.TryAdd(id, true);
        Cog.Information($"Removed id '{id}'");
    }

    public void CancelAllRemovedButRunning()
    {
        foreach (var removedId in idIsRemoved.Where(x => x.Value is true))
        {
            if (idToLatest.TryRemove(removedId.Key, out var latest))
            {
                latest.cts.Cancel();
                latest.cts.Dispose();
            }
        }
    }

    async ValueTask ConsumeQueueAsync()
    {
        await foreach (var (id, func, cts) in _queue.Reader.ReadAllAsync())
        {
            if (idIsRemoved.TryGetValue(id, out var isRemoved) && isRemoved)
            {
                idToLatest.TryRemove(id, out _);
                continue;
            }

            var prev = (func, cts);
            try
            {
                await func(cts.Token);

                while (idToLatest.TryRemove(id, out var latest) && !latest.Equals(prev))
                {
                    await latest.func(latest.cts.Token);
                    prev = (latest.func, latest.cts);
                    Cog.Information($"Updating new after old finished with id '{id}'");
                }

                Cog.Information($"Finished most up-to-date with id '{id}'");
            }
            catch (OperationCanceledException)
            {
                EnqueueIfNewAvailable(id, prev);
            }
            catch (Exception ex)
            {
                EnqueueIfNewAvailable(id, prev);
                Cog.Error($"Download Manager Queue: {ex.Message}");
            }
        }
    }

    void EnqueueIfNewAvailable(
        string id,
        (Func<CancellationToken, Task> func, CancellationTokenSource cts) prev
    )
    {
        idToLatest.TryRemove(id, out var latest);
        if (latest.Equals(prev))
            return;

        Enqueue(id, latest.func, latest.cts.Token);
    }
}
