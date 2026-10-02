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

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath)
    {
        Utils.CopyDirectory(modLoaderFilesPath, gameRootPath, recursive: false);
    }

    public PackageInstaller GetInstaller(PackageVersionReference packageVersion)
    {
        if (packageVersion.FullName == "Thunderstore-lovely")
            return lovelyInstaller;

        return packageInstaller;
    }

    public List<string> GetLaunchArguments(LazyModList modList)
    {
        throw new NotImplementedException();
    }
}
