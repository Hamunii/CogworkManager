namespace Cogwork.Gui;

public static class Extensions
{
    public static void EnqueueGtk<T>(
        this TaskManager manager,
        string id,
        Func<CancellationToken, Task<T>> job,
        Action<T> gtk,
        CancellationToken cancellationToken
    )
    {
        manager.Enqueue(
            id,
            async (ct) =>
            {
                var t = await job(ct);
                GLib.Functions.TimeoutAdd(
                    0,
                    0,
                    () =>
                    {
                        gtk(t);
                        return false;
                    }
                );
            },
            cancellationToken
        );
    }
}
