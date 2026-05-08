global using static Cogwork.Core.CogworkCoreLogger;
using System.Globalization;
using System.Reflection;
using Serilog;
using Serilog.Core;

namespace Cogwork.Core;

public static class CogworkCoreLogger
{
#pragma warning disable IDE0052 // Private member can be removed as the value assigned to it is never read
    static readonly Finalizer finalizer = new();
#pragma warning restore

    sealed class Finalizer
    {
        ~Finalizer()
        {
            LogMutex.ReleaseMutex();
            LogMutex.Dispose();
        }
    }

    static CogworkCoreLogger()
    {
        var assembly = typeof(CogworkCoreLogger).Assembly;
        var name = assembly.GetName().Name;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        string mutexId = $@"Global\Hamunii.Cogwork.Logger";
        int numId = 0;
        bool createdNew = false;
        while (true)
        {
            Mutex mutex = new(true, mutexId + numId, out createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                numId++;
            }
            else
            {
                LogMutex = mutex;
                break;
            }
        }

        LogFileLocation = Path.Combine(
            CogworkPaths.GetCacheSubDirectory("logs"),
            $"instance-{numId}-date-.log"
        );

        Cog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
#if DEBUG
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
#else
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error,
#endif
                formatProvider: CultureInfo.InvariantCulture
            )
            .WriteTo.File(
                LogFileLocation,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 2,
                buffered: false
            )
            .CreateLogger();

        Cog.Debug($"=============================");
        Cog.Debug($"{name} {version} initialized.");
    }

    public static Logger Cog { get; }
    static string LogFileLocation { get; }
    static Mutex LogMutex { get; }
}
