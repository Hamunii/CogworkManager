using System.IO.Abstractions;

namespace Cogwork.Core.Installers;

public interface IModInstallers
{
    public static FileSystem RealFileSystem { get; } = new FileSystem();

    PackageInstaller GetInstaller(PackageVersionReference packageVersion);
    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath);
    public string[] GetProxyFiles();
    public string[] GetLaunchArgs(LazyModList modList);
    public string[] GetDirectLaunchArgs(LazyModList modList);
    public string? GetLogPath(LazyModList modList);

    public static string GetGameExecutableOrThrow(bool isLinuxApp, string gamePath)
    {
        var executables = Directory
            .GetFiles(gamePath)
            .Where(x =>
            {
                var ext = Path.GetExtension(x);

                if (!isLinuxApp)
                {
                    if (ext is ".exe" && Path.GetFileName(x) is not "UnityCrashHandler64.exe")
                        return true;

                    return false;
                }

                if (ext is ".x86_64" or ".x86")
                    return true;

                if (Path.GetFileName(x) == Path.GetFileName(gamePath))
                    return true;

                return false;
            })
            .ToArray();

        if (executables.Length > 1)
        {
            throw new FileNotFoundException(
                $"Too many executable candidates: '{string.Join("', '", executables)}'"
            );
        }
        else if (executables.Length == 0)
        {
            throw new FileNotFoundException($"No exes found at '{gamePath}'");
        }

        var gameExecutable = executables[0];
        return gameExecutable;
    }
}
