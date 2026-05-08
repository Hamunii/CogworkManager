using System.Diagnostics.CodeAnalysis;
using Cogwork.Core.Extensions;

namespace Cogwork.Core;

public readonly record struct PerformedOrNot<T>(
    [property: MemberNotNullWhen(true, nameof(PerformedOrNot<>.Value))] bool Performed,
    T? Value = default
);

public static class Utils
{
    // Apparently one should preferably keep a singleton of HttpClient.
    internal static HttpClient SharedHttpClient { get; } = new();

    /// <summary>
    /// Performs a task if it's not actively being
    /// performed by another thread or application instance,
    /// otherwise waits until the preexisting task is finished.
    /// </summary>
    public static async Task<PerformedOrNot<T>> DoTaskOrWaitForCompletionAsync<T>(
        string lockDirectory,
        ProgressContext progress,
        Func<ProgressContext, Task<T>> doTask
    )
    {
        if (!Directory.Exists(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        bool waitedForLock = false;
        while (true)
        {
            try
            {
                using FileStream lockFile = new(
                    Path.Combine(lockDirectory, ".lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.None
                );

                if (waitedForLock)
                {
                    Cog.Warning($"Got lock for '{lockDirectory}'");
                    return new(false);
                }
                return new(true, await doTask(progress));
            }
            catch (IOException)
            {
                if (!waitedForLock)
                {
                    waitedForLock = true;
                    Cog.Warning($"Awaiting lock for '{lockDirectory}' instead of fetching");
                }
                await Task.Delay(100);
            }
        }
    }
}
