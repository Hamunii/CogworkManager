using System.IO.Abstractions;

namespace Cogwork.Core.InstallRules;

public interface IModInstallRules
{
    public static FileSystem RealFileSystem { get; } = new FileSystem();

    PackageInstaller GetInstaller(PackageVersionReference packageVersion);

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath);

    public List<string> GetLaunchArguments(LazyModList modList);
}
