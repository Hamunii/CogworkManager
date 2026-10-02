namespace Cogwork.Core.Installers;

public class LovelyInstallers(PackageInstaller packageInstaller, PackageInstaller lovelyInstaller)
    : IModInstallers
{
    public static LovelyInstallers Default { get; } =
        new(
            new([new(string.Empty, "mods", InstallType.Namespaced)], protectedDirs: []),
            PackageInstaller.GenericExactFileInstaller
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
