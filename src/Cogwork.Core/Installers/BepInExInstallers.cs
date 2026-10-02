using System.Runtime.InteropServices;
using ZLinq;

namespace Cogwork.Core.Installers;

public class BepInExInstallers(PackageInstaller packageInstaller, PackageInstaller bepInExInstaller)
    : IModInstallers
{
    // https://github.com/ebkr/r2modmanPlus/wiki/Structuring-your-Thunderstore-package
    public static BepInExInstallers Default { get; } =
        new(
            new(
                [
                    new("core", Path.Combine("BepInEx", "core")),
                    new("patchers", Path.Combine("BepInEx", "patchers")),
                    new("plugins", Path.Combine("BepInEx", "plugins")),
                    new("monomod", Path.Combine("BepInEx", "monomod"), [".mm.dll"]),
                    new("config", Path.Combine("BepInEx", "config"), InstallType.Direct),
                ],
                protectedDirs: [Path.Combine("BepInEx", "config")]
            ),
            PackageInstaller.SimpleDirectSkipRootInstaller
        );

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

    public List<string> GetLaunchArguments(LazyModList modList)
    {
        var isLinuxApp = modList.IsLinuxNative();

        var profileFiles = modList.ProfileFilesDirectory;
        var gamePath = modList.GetGamePathOrThrow();
        string gameExecutable = IModInstallers.GetGameExecutableOrThrow(isLinuxApp, gamePath);

        List<string> args = [];

        if (isLinuxApp)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // This is bad error handling, but I'd assume this should never happen.
                throw new InvalidOperationException(
                    "This game is not supported on Windows (as far as Cogwork Manager is aware)."
                );
            }

            var runBepInExPath = Path.Combine(profileFiles, "run_bepinex.sh");
            args.Add(runBepInExPath);

            UnixFileMode currentMode = File.GetUnixFileMode(runBepInExPath);
            File.SetUnixFileMode(runBepInExPath, currentMode | UnixFileMode.UserExecute);
        }

        // <path to game> [doorstop arguments]
        args.Add(gameExecutable);
        args.Add("--doorstop-enabled");
        args.Add("true");
        args.Add("--doorstop-target-assembly");
        args.Add(Path.Combine(profileFiles, "BepInEx", "core", "BepInEx.Preloader.dll"));

        return args;
    }
}
