using System.Runtime.InteropServices;

namespace Cogwork.Core.Installers;

public class LovelyInstallers(PackageInstaller packageInstaller, PackageInstaller lovelyInstaller)
    : IModInstallers
{
    public static PackageInstaller DefaultLovelyInstaller { get; } =
        new(
            [
                SourceToDestination.None(),
                new("lovely", Path.Combine("mods", "lovely"), InstallType.Direct),
                SourceToDestination.FileMapping("winhttp.dll"),
                SourceToDestination.FileMapping("version.dll"),
                SourceToDestination.FileMapping("winmm.dll"),
            ],
            protectedDirs: []
        );

    public static LovelyInstallers Default { get; } =
        new(
            new([new(string.Empty, "mods", InstallType.Namespaced)], protectedDirs: []),
            DefaultLovelyInstaller
        );

    public PackageInstaller GetInstaller(PackageVersionReference packageVersion)
    {
        if (packageVersion.FullName == "Thunderstore-lovely")
            return lovelyInstaller;

        return packageInstaller;
    }

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath)
    {
        Utils.CopyDirectory(modLoaderFilesPath, gameRootPath, recursive: false);
    }

    public string[] GetProxyFiles() => ["winmm.dll"];

    public string[] GetLaunchArgs(LazyModList modList)
    {
        var mods = Path.Combine(modList.HostProfileFilesDirectory, "mods");
        modList.FormatIfProton(ref mods);

        return ["--mod-dir", mods];
    }

    public string[] GetDirectLaunchArgs(LazyModList modList)
    {
        var isLinuxApp = modList.IsLinuxNative();
        var gamePath = modList.GetGamePathOrThrow();
        string gameExecutable = IModInstallers.GetGameExecutableOrThrow(isLinuxApp, gamePath);

        return [gameExecutable];
    }

    public string? GetLogPath(LazyModList modList) => null;
}
