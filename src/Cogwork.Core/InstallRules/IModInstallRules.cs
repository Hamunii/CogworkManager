using System.IO.Abstractions;

namespace Cogwork.Core.InstallRules;

public interface IModInstallRules
{
    public static FileSystem RealFileSystem { get; } = new FileSystem();

    static abstract string InstallRootDirectory { get; }

    string[] Map(
        ModList modList,
        PackageVersionReference packageVersion,
        string directoryPath,
        string outputPath
    );

    public Task<FileInstalls?> InstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Uninstalls the target package from a profile, or nothing if it's not installed.
    /// </summary>
    /// <remarks>
    /// This never fails, however the install map must be accurate in order to
    /// not corrupt the installed files tracking data.
    /// </remarks>
    /// <returns>Null.</returns>
    public Task<FileInstalls?> UninstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        Dictionary<PackageVersionReference, FileInstalls?>? installMap,
        CancellationToken cancellationToken = default
    );

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath);

    public List<string> GetLaunchArguments(LazyModList modList);
}
