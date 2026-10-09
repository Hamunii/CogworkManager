using System.Runtime.InteropServices;

namespace Cogwork.Core.Installers;

public class BepInExInstallers(PackageInstaller packageInstaller, PackageInstaller bepInExInstaller)
    : IModInstallers
{
    // https://github.com/ebkr/r2modmanPlus/wiki/Structuring-your-Thunderstore-package
    public static PackageInstaller DefaultPackageInstaller { get; } =
        new(
            [
                Mapping.NamespacedFlattened("plugins", "BepInEx/plugins"),
                Mapping.NamespacedFlattened("core", "BepInEx/core"),
                Mapping.NamespacedFlattened("patchers", "BepInEx/patchers"),
                Mapping.NamespacedFlattened("monomod", "BepInEx/monomod", [".mm.dll"]),
                Mapping.Direct("config", "BepInEx/config"),
            ],
            protectedDirs: ["BepInEx/config"]
        );

    public static BepInExInstallers Default { get; } =
        new(DefaultPackageInstaller, PackageInstaller.GenericDirectSkipRootInstaller);

    public PackageInstaller GetInstaller(PackageVersionReference packageVersion)
    {
        if (IsBepInExPackage(packageVersion))
            return bepInExInstaller;

        return packageInstaller;
    }

    // TODO: Use proper detection of BepInEx package for a Thunderstore community.
    static bool IsBepInExPackage(PackageVersionReference package)
    {
        var resolved = package.Resolve();
        if (!resolved.Name.StartsWith("BepInExPack", StringComparison.OrdinalIgnoreCase))
            return false;

        return resolved.Author.Name switch
        {
            "BepInEx" => true, // Default
            "bbepis" => true, // Risk of Rain 2
            "denikson" => true, // Valheim
            "silksong_modding" => true, // Silksong
            _ => false,
        };
    }

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath)
    {
        Utils.CopyDirectory(modLoaderFilesPath, gameRootPath, recursive: false);
    }

    public string[] GetProxyFiles() => ["winhttp"];

    public string[] GetLaunchArgs(LazyModList modList)
    {
        var preloader = Path.Combine(
            modList.HostProfileFilesDirectory,
            "BepInEx",
            "core",
            "BepInEx.Preloader.dll"
        );
        modList.FormatIfProton(ref preloader);

        return ["--doorstop-enabled", "true", "--doorstop-target-assembly", preloader];
    }

    public string[] GetDirectLaunchArgs(LazyModList modList)
    {
        var isLinuxApp = modList.IsLinuxNative();
        var gamePath = modList.GetGamePathOrThrow();
        string gameExecutable = IModInstallers.GetGameExecutableOrThrow(isLinuxApp, gamePath);

        if (!isLinuxApp)
        {
            return [gameExecutable];
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // This is bad error handling, but I'd assume this should never happen.
            throw new InvalidOperationException(
                "This game is not supported on Windows (as far as Cogwork Manager is aware)."
            );
        }

        var runBepInExPath = Path.Combine(modList.ProfileFilesDirectory, "run_bepinex.sh");
        UnixFileMode currentMode = File.GetUnixFileMode(runBepInExPath);
        File.SetUnixFileMode(runBepInExPath, currentMode | UnixFileMode.UserExecute);

        return [runBepInExPath, gameExecutable];
    }

    public string? GetLogPath(LazyModList modList) =>
        Path.Combine(modList.ProfileFilesDirectory, "BepInEx", "LogOutput.log");
}
