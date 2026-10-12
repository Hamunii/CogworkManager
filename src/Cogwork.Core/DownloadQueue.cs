using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Cogwork.Core;

/// <summary>
/// Performs async tasks deduplicating executing tasks by id.
/// </summary>
public class TaskManager
{
    public static TaskManager IconQueue { get; } = new(20);

    readonly Channel<(string id, Func<CancellationToken, Task> func, CancellationToken ct)> _queue;
    readonly Task[] _workers;

    readonly ConcurrentDictionary<
        string,
        (Func<CancellationToken, Task> func, CancellationToken ct)
    > idToLatest = [];

    public TaskManager(int capacity)
    {
        BoundedChannelOptions options = new(capacity) { FullMode = BoundedChannelFullMode.Wait };
        _queue = Channel.CreateBounded<(string, Func<CancellationToken, Task>, CancellationToken)>(
            options
        );

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
        var value = (job, cancellationToken);

        idToLatest.AddOrUpdate(
            id,
            addValueFactory: (id) =>
            {
                _ = Task.Run(() => _queue.Writer.WriteAsync((id, job, cancellationToken)));
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

    async ValueTask ConsumeQueueAsync()
    {
        await foreach (var (id, func, ct) in _queue.Reader.ReadAllAsync())
        {
            var prev = (func, ct);
            try
            {
                await func(ct);

                while (idToLatest.TryRemove(id, out var latest) && !latest.Equals(prev))
                {
                    await latest.func(latest.ct);
                    prev = (latest.func, latest.ct);
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
        (Func<CancellationToken, Task> func, CancellationToken ct) prev
    )
    {
        idToLatest.TryRemove(id, out var latest);
        if (latest.Equals(prev))
            return;

        Enqueue(id, latest.func, latest.ct);
    }
}
