namespace Cogwork.Core.InstallRules;

public readonly record struct LovelyInstaller : IModInstallRules
{
    public static string InstallRootDirectory => throw new NotImplementedException();

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath)
    {
        throw new NotImplementedException();
    }

    public List<string> GetLaunchArguments(LazyModList modList)
    {
        throw new NotImplementedException();
    }

    public Task<FileInstalls?> InstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    )
    {
        throw new NotImplementedException();
    }

    public string[] Map(
        ModList modList,
        PackageVersionReference packageVersion,
        string directoryPath,
        string outputPath
    )
    {
        throw new NotImplementedException();
    }

    public Task<FileInstalls?> UninstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        Dictionary<PackageVersionReference, FileInstalls?>? installMap,
        CancellationToken cancellationToken = default
    )
    {
        throw new NotImplementedException();
    }
}
