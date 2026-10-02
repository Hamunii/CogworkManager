using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using ZLinq;

namespace Cogwork.Core.InstallRules;

public enum InstallType
{
    Direct,
    DirectSkipRoot,
    Namespaced,
    NamespacedFlattened,
}

public readonly record struct SourceToDestination(
    string Source,
    string Destination,
    InstallType Type,
    string[]? DefaultExtensions = null
)
{
    public SourceToDestination(
        string Source,
        string Destination,
        string[]? DefaultExtensions = null
    )
        : this(Source, Destination, InstallType.NamespacedFlattened, DefaultExtensions) { }
}

public readonly record struct Mapping(string Destination, InstallType Type);

public record PackageInstaller
{
    static readonly FileSystem Fs = IModInstallRules.RealFileSystem;
    readonly Dictionary<string, Mapping> DirToDir;
    readonly Dictionary<string, Mapping> ExtensionToDir;
    readonly HashSet<string> ProtectedDirsFromRemoval;

    public PackageInstaller(SourceToDestination[] dirToDir, string[] protectedDirs)
    {
        DirToDir = new(
            dirToDir.Select(x => new KeyValuePair<string, Mapping>(
                x.Source,
                new(x.Destination, x.Type)
            )),
            StringComparer.OrdinalIgnoreCase
        );
        ExtensionToDir = new(
            dirToDir
                .Where(x => x.DefaultExtensions is { })
                .SelectMany(x =>
                    x.DefaultExtensions!.Select(extension => new KeyValuePair<string, Mapping>(
                        extension,
                        new(x.Destination, x.Type)
                    ))
                ),
            StringComparer.OrdinalIgnoreCase
        );
        ProtectedDirsFromRemoval = new(protectedDirs, StringComparer.OrdinalIgnoreCase);
    }

    public static PackageInstaller SimpleDirectSkipRootInstaller { get; } =
        new(
            [new(string.Empty, string.Empty, InstallType.DirectSkipRoot)],
            protectedDirs: [Path.Combine("BepInEx", "config")]
        );

    public Mapping GetDefaultMapping() => DirToDir.First().Value;

    public string[] Map(
        PackageVersionReference packageVersion,
        string directoryPath,
        string outputPath
    )
    {
        HashSet<string> mapped = [];
        MapRecursive(packageVersion, directoryPath, outputPath, mapped);
        Fs.Directory.Delete(directoryPath, recursive: true);
        return [.. mapped];
    }

    private void MapRecursive(
        PackageVersionReference package,
        string directoryPath,
        string outputPath,
        HashSet<string> mappedFiles
    )
    {
        foreach (var dirPath in Fs.Directory.EnumerateDirectories(directoryPath))
        {
            var dirName = Path.GetFileName(dirPath);
            if (DirToDir.TryGetValue(dirName, out var mapping))
            {
                var mapped = Path.Combine(outputPath, mapping.Destination);
                switch (mapping.Type)
                {
                    case InstallType.Direct:
                    case InstallType.DirectSkipRoot:
                        MoveOrMergeOverwrite(dirPath, mapped, mappedFiles);
                        continue;

                    case InstallType.Namespaced:
                    case InstallType.NamespacedFlattened:
                        mapped = Path.Combine(outputPath, mapping.Destination);
                        var namespaced = Path.Combine(mapped, package.FullName);
                        Fs.Directory.CreateDirectory(mapped);
                        MoveOrMergeOverwrite(dirPath, namespaced, mappedFiles);
                        continue;

                    default:
                        throw new NotImplementedException("Install type is not implemented.");
                }
            }

            MapRecursive(package, dirPath, outputPath, mappedFiles);
        }

        var defaultMapping = GetDefaultMapping();
        switch (defaultMapping.Type)
        {
            case InstallType.Direct:
                MoveOrMergeOverwrite(
                    directoryPath,
                    Path.Combine(outputPath, defaultMapping.Destination),
                    mappedFiles
                );
                break;

            case InstallType.DirectSkipRoot:
                var first = Fs.Directory.EnumerateDirectories(directoryPath).FirstOrDefault();
                if (first is null)
                    break;

                MoveOrMergeOverwrite(
                    first,
                    Path.Combine(outputPath, defaultMapping.Destination),
                    mappedFiles
                );
                break;

            case InstallType.Namespaced:
                MoveOrMergeOverwrite(
                    directoryPath,
                    Path.Combine(outputPath, defaultMapping.Destination, package.FullName),
                    mappedFiles
                );
                break;

            case InstallType.NamespacedFlattened:
                var defaultFlattenDir = Path.Combine(
                    outputPath,
                    defaultMapping.Destination,
                    package.FullName
                );
                Fs.Directory.CreateDirectory(defaultFlattenDir);

                foreach (var fileDir in Fs.Directory.EnumerateFiles(directoryPath))
                {
                    var fileName = Path.GetFileName(fileDir);

                    string? dest = null;
                    foreach (var (extension, mapping) in ExtensionToDir)
                    {
                        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var destDir = Path.Combine(
                            outputPath,
                            mapping.Destination,
                            package.FullName
                        );
                        Fs.Directory.CreateDirectory(destDir);
                        dest = Path.Combine(destDir, fileName);
                        break;
                    }
                    dest ??= Path.Combine(defaultFlattenDir, fileName);
                    MoveFile(mappedFiles, fileDir, dest);
                }
                break;

            default:
                throw new NotImplementedException("Install type is not implemented.");
        }
    }

    static void MoveOrMergeOverwrite(
        string sourceDirName,
        string destDirName,
        HashSet<string> mappedFiles
    )
    {
        if (!Fs.Directory.Exists(destDirName))
        {
            Fs.Directory.CreateDirectory(destDirName);
        }

        foreach (var file in Fs.Directory.EnumerateFiles(sourceDirName))
        {
            var fileName = Path.GetFileName(file);
            var dest = Path.Combine(destDirName, fileName);
            if (Fs.File.Exists(dest))
            {
                Fs.File.Delete(dest);
            }
            MoveFile(mappedFiles, file, dest);
        }

        foreach (var dir in Fs.Directory.EnumerateDirectories(sourceDirName))
        {
            var dirName = Path.GetFileName(dir);
            MoveOrMergeOverwrite(dir, Path.Combine(destDirName, dirName), mappedFiles);
        }

        Fs.Directory.Delete(sourceDirName);
    }

    private static void MoveFile(HashSet<string> mappedFiles, string file, string dest)
    {
        if (mappedFiles.Contains(dest))
        {
            Cog.Information(
                $"Conflict in mapping package files to profile: '{dest}' already mapped; not overwriting it."
            );
            return;
        }
        Fs.File.Move(file, dest, overwrite: true);
        mappedFiles.Add(dest);
    }

    public async Task<FileInstalls?> InstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    )
    {
        if (ShouldIgnorePackage(modList, packageVersion))
        {
            return new FileInstalls([], []);
        }

        var path = await packageVersion.Source.ExtractAsync(packageVersion, cancellationToken);
        if (path is null)
        {
            Cog.Error($"Cannot install package which is not downloaded: '{packageVersion}'");
            return null;
        }

        Directory.CreateDirectory(profileFilesDirectory);
        var pathCopy = path + ".temp";

        Fs.Directory.CreateDirectory(pathCopy);
        CopyDirectory(path, pathCopy);
        var mapped = Map(packageVersion, pathCopy, profileFilesDirectory);

        return new FileInstalls(mapped, []);
    }

    private static bool ShouldIgnorePackage(
        ModList modList,
        PackageVersionReference packageVersion
    ) =>
        modList.Game == Game.Silksong // Silksong has a replacement package, and these are incompatible.
        && packageVersion.FullName.Equals("BepInEx-BepInExPack_Silksong", StringComparison.Ordinal);

    /// <summary>
    /// Uninstalls the target package from a profile, or nothing if it's not installed.
    /// </summary>
    /// <remarks>
    /// This never fails, however the install map must be accurate in order to
    /// not corrupt the installed files tracking data.
    /// </remarks>
    /// <returns>Null.</returns>
    public async Task<FileInstalls?> UninstallPackageAsync(
        ModList modList,
        PackageVersionReference packageVersion,
        string profileFilesDirectory,
        Dictionary<PackageVersionReference, FileInstalls?>? installMap,
        CancellationToken cancellationToken = default
    )
    {
        if (
            installMap is null
            || !installMap.TryGetValue(packageVersion, out var fileInstallsOrNull)
            || fileInstallsOrNull is not { } fileInstalls
        )
        {
            // Was not installed
            return null;
        }

        var fakeFs = new MockFileSystem(
            fileInstalls.Installed.ToDictionary(
                keySelector: x =>
                {
                    if (!x.StartsWith(profileFilesDirectory, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Corrupt installed file data; file to delete ('{x}') is not within '{profileFilesDirectory}'"
                        );
                    }
                    return x;
                },
                elementSelector: x => new MockFileData(string.Empty)
            )
        );

        foreach (var dir in ProtectedDirsFromRemoval)
        {
            var protectedDirPath = Path.Combine(profileFilesDirectory, dir);
            if (fakeFs.Directory.Exists(protectedDirPath))
            {
                fakeFs.Directory.Delete(protectedDirPath, recursive: true);
            }
        }

        // Then we just delete the fake mapped files from our real filesystem.
        if (fakeFs.Directory.Exists(profileFilesDirectory))
        {
            DeleteDirectoryContentsBasedOnSource(fakeFs, profileFilesDirectory);
        }

        return null;
    }

    static void CopyDirectory(string sourceDirName, string destDirName)
    {
        foreach (var file in Fs.Directory.EnumerateFiles(sourceDirName).AsValueEnumerable())
        {
            var fileName = Path.GetFileName(file);
            // TODO: Do not overwrite without confirmation.
            // This will overwrite config files if packages ship them.
            Fs.File.Copy(file, Path.Combine(destDirName, fileName), overwrite: true);
        }

        foreach (var dir in Fs.Directory.EnumerateDirectories(sourceDirName).AsValueEnumerable())
        {
            var dirName = Path.GetFileName(dir);
            var newDir = Path.Combine(destDirName, dirName);
            Fs.Directory.CreateDirectory(newDir);
            CopyDirectory(dir, newDir);
        }
    }

    static void DeleteDirectoryContentsBasedOnSource(IFileSystem sourceFs, string dir)
    {
        foreach (var file in sourceFs.Directory.EnumerateFiles(dir).AsValueEnumerable())
        {
            if (File.Exists(file))
            {
                // TODO: Do not delete config files without confirmation.
                File.Delete(file);
            }
        }

        foreach (var subDir in sourceFs.Directory.EnumerateDirectories(dir).AsValueEnumerable())
        {
            if (Directory.Exists(subDir))
            {
                DeleteDirectoryContentsBasedOnSource(sourceFs, subDir);
            }
        }

        if (Directory.GetFileSystemEntries(dir).Length == 0)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir);
            }
        }
    }
}
