using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using ZLinq;

namespace Cogwork.Core.Installers;

public enum InstallType
{
    None,
    Direct,
    DirectSkipRoot,
    Namespaced,
    NamespacedFlattened,
    File,
}

public readonly record struct Mapping
{
    public readonly string Source { get; }
    public readonly string Destination { get; }
    public readonly InstallType Type { get; }
    public readonly string[]? DefaultExtensions { get; }

    Mapping(string source, string destination, InstallType type, string[]? defaultExtensions = null)
    {
        Source = source;
        Debug.Assert(destination.Contains('\\') is false);
        Destination = destination.Replace('/', Path.PathSeparator);
        Type = type;
        DefaultExtensions = defaultExtensions;
    }

    public static Mapping None() => new(string.Empty, string.Empty, InstallType.None);

    public static Mapping Direct(string source, string destination) =>
        new(source, destination, InstallType.Direct);

    public static Mapping DirectSkipRoot(string source, string destination) =>
        new(source, destination, InstallType.DirectSkipRoot);

    public static Mapping Namespaced(string source, string destination) =>
        new(source, destination, InstallType.Namespaced);

    public static Mapping NamespacedFlattened(
        string Source,
        string Destination,
        string[]? DefaultExtensions = null
    ) => new(Source, Destination, InstallType.NamespacedFlattened, DefaultExtensions);

    public static Mapping File(string fileSourceToDestination) =>
        new(fileSourceToDestination, fileSourceToDestination, InstallType.File);

    public static Mapping File(string fileSource, string fileDestination) =>
        new(fileSource, fileDestination, InstallType.File);
}

public readonly record struct MappingInfo(string Destination, InstallType Type);

public class PackageInstaller
{
    static readonly FileSystem Fs = IModInstallers.RealFileSystem;
    readonly Dictionary<string, MappingInfo> DirToDir;
    readonly Dictionary<string, MappingInfo> ExtensionToDir;
    readonly HashSet<string> ProtectedDirsFromRemoval;

    public PackageInstaller(Mapping[] dirToDir, string[] protectedDirs)
    {
        DirToDir = new(
            dirToDir.Select(x => new KeyValuePair<string, MappingInfo>(
                x.Source,
                new(x.Destination, x.Type)
            )),
            StringComparer.OrdinalIgnoreCase
        );
        ExtensionToDir = new(
            dirToDir
                .Where(x => x.DefaultExtensions is { })
                .SelectMany(x =>
                    x.DefaultExtensions!.Select(extension => new KeyValuePair<string, MappingInfo>(
                        extension,
                        new(x.Destination, x.Type)
                    ))
                ),
            StringComparer.OrdinalIgnoreCase
        );

        Debug.Assert(protectedDirs.All(x => x.Contains('\\') is false));
        ProtectedDirsFromRemoval = new(
            protectedDirs.Select(x => x.Replace('/', Path.PathSeparator)),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public static PackageInstaller GenericDirectSkipRootInstaller { get; } =
        new(
            [Mapping.DirectSkipRoot(string.Empty, string.Empty)],
            protectedDirs: [Path.Combine("BepInEx", "config")]
        );

    /// <remarks>
    /// The default mapping is always implicitly the first entry,
    /// and it's always expected that there is at least one entry.
    /// Use <see cref="Mapping.None"/> to set a no-op default mapping.
    /// </remarks>
    public MappingInfo GetDefaultMapping() => DirToDir.First().Value;

    public string[] Map(PackageVersionReference package, string directoryPath, string outputPath)
    {
        HashSet<string> mapped = [];
        MapRecursive(package, directoryPath, outputPath, mapped);

        var defaultMapping = GetDefaultMapping();
        switch (defaultMapping.Type)
        {
            case InstallType.None:
                break;

            case InstallType.Direct:
                MoveOrMergeOverwrite(
                    directoryPath,
                    Path.Combine(outputPath, defaultMapping.Destination),
                    mapped
                );
                break;

            case InstallType.DirectSkipRoot:
                var first = Fs.Directory.EnumerateDirectories(directoryPath).FirstOrDefault();
                if (first is null)
                    break;

                MoveOrMergeOverwrite(
                    first,
                    Path.Combine(outputPath, defaultMapping.Destination),
                    mapped
                );
                break;

            case InstallType.Namespaced:
                MoveOrMergeOverwrite(
                    directoryPath,
                    Path.Combine(outputPath, defaultMapping.Destination, package.FullName),
                    mapped
                );
                break;

            case InstallType.NamespacedFlattened:
            case InstallType.File:
                break;

            default:
                throw new NotImplementedException("Install type is not implemented.");
        }

        foreach (var (filePath, mapping) in DirToDir)
        {
            if (mapping.Type is not InstallType.File)
                continue;

            var sourcePath = Path.Combine(directoryPath, filePath);
            if (!Fs.File.Exists(sourcePath))
                continue;

            MoveFile(mapped, sourcePath, Path.Combine(outputPath, mapping.Destination));
        }

        if (Fs.Directory.Exists(directoryPath))
        {
            Fs.Directory.Delete(directoryPath, recursive: true);
        }
        return [.. mapped];
    }

    /// <summary>
    /// Recursively go through every directory until we come across
    /// a special directory which performs a mapping on the directory
    /// and then escapes the recursive loop, then the default mapping is
    /// performed on the provided root before returning from this method.
    /// </summary>
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
            if (!DirToDir.TryGetValue(dirName, out var mapping))
            {
                MapRecursive(package, dirPath, outputPath, mappedFiles);
                break;
            }

            var mapped = Path.Combine(outputPath, mapping.Destination);
            switch (mapping.Type)
            {
                case InstallType.None:
                case InstallType.File:
                    break;

                case InstallType.Direct:
                case InstallType.DirectSkipRoot:
                    MoveOrMergeOverwrite(dirPath, mapped, mappedFiles);
                    break;

                case InstallType.Namespaced:
                case InstallType.NamespacedFlattened:
                    var namespaced = Path.Combine(mapped, package.FullName);
                    MoveOrMergeOverwrite(dirPath, namespaced, mappedFiles);
                    break;

                default:
                    throw new NotImplementedException("Install type is not implemented.");
            }
            break;
        }

        var defaultMapping = GetDefaultMapping();
        switch (defaultMapping.Type)
        {
            case InstallType.None:
            case InstallType.File:
            case InstallType.Direct:
            case InstallType.DirectSkipRoot:
            case InstallType.Namespaced:
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

        Utils.CopyDirectory(path, pathCopy, recursive: true);
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
