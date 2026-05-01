using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using ZLinq;

namespace Cogwork.Core;

public interface IModInstallRules
{
    public static FileSystem RealFileSystem { get; } = new FileSystem();

    static abstract string InstallRootDirectory { get; }

    bool Map(VisualPackageVersion packageVersion, string directoryPath, string outputPath);

    public Task<bool> InstallPackageAsync(
        VisualPackageVersion packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    );

    public Task<bool> UninstallPackageAsync(
        VisualPackageVersion packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    );

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath);

    public List<string> GetLaunchArguments(LazyModList modList);
}

public readonly record struct BepInExModInstallRules(IFileSystem Fs) : IModInstallRules
{
    // https://github.com/ebkr/r2modmanPlus/wiki/Structuring-your-Thunderstore-package
    static readonly HashSet<string> dirToDir = new(["config"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> dirToDirPlusPackageName = new(
        ["core", "patchers", "plugins", "monomod"],
        StringComparer.OrdinalIgnoreCase
    );
    const string defaultDir = "plugins";
    public static string InstallRootDirectory { get; } = "BepInEx";

    // TODO: Use proper detection of BepInEx package for a Thunderstore community.
    static bool IsBepInExPackage(VisualPackageVersion package) =>
        package.Name.StartsWith("BepInExPack", StringComparison.OrdinalIgnoreCase)
        && package.Author.Name
            is "BepInEx" // Default
                or "bbepis" // Risk of Rain 2
                or " denikson" // Valheim
    ;

    public BepInExModInstallRules()
        : this(IModInstallRules.RealFileSystem) { }

    public void CopyModLoaderFilesToGame(string modLoaderFilesPath, string gameRootPath)
    {
        foreach (var fileDir in Fs.Directory.GetFiles(modLoaderFilesPath).AsValueEnumerable())
        {
            var fileName = Path.GetFileName(fileDir);
            Fs.File.Copy(fileDir, Path.Combine(gameRootPath, fileName), true);
        }
    }

    public bool Map(VisualPackageVersion packageVersion, string directoryPath, string outputPath)
    {
        if (IsBepInExPackage(packageVersion))
        {
            IgnoreUntilWinhttpThenMap(directoryPath, outputPath, foundWinhttpDll: false);
            Fs.Directory.Delete(directoryPath, recursive: true);
            return true;
        }

        Fs.Directory.CreateDirectory(Path.Combine(outputPath, defaultDir, packageVersion.FullName));
        MapRecursive(packageVersion, directoryPath, outputPath);
        Fs.Directory.Delete(directoryPath, recursive: true);
        return true;
    }

    private void IgnoreUntilWinhttpThenMap(
        string directoryPath,
        string outputPath,
        bool foundWinhttpDll
    )
    {
        Fs.Directory.CreateDirectory(outputPath);

        if (!foundWinhttpDll)
        {
            foreach (var fileDir in Fs.Directory.EnumerateFiles(directoryPath).AsValueEnumerable())
            {
                if (Path.GetFileName(fileDir) == "winhttp.dll")
                {
                    foundWinhttpDll = true;
                    break;
                }
            }
        }

        if (foundWinhttpDll)
        {
            foreach (var fileDir in Fs.Directory.EnumerateFiles(directoryPath).AsValueEnumerable())
            {
                var fileName = Path.GetFileName(fileDir);
                var dest = Path.Combine(outputPath, fileName);
                if (Fs.File.Exists(dest))
                {
                    Fs.File.Delete(dest);
                }
                Fs.File.Move(fileDir, dest);
            }
        }

        foreach (var dir in Fs.Directory.EnumerateDirectories(directoryPath).AsValueEnumerable())
        {
            var dirName = Path.GetFileName(dir);

            if (foundWinhttpDll)
                IgnoreUntilWinhttpThenMap(dir, Path.Combine(outputPath, dirName), foundWinhttpDll);
            else
                IgnoreUntilWinhttpThenMap(dir, outputPath, foundWinhttpDll);
        }
    }

    private void MapRecursive(VisualPackageVersion package, string directoryPath, string outputPath)
    {
        foreach (
            var dirPath in Fs.Directory.EnumerateDirectories(directoryPath).AsValueEnumerable()
        )
        {
            var dirName = Path.GetFileName(dirPath).ToLowerInvariant();

            if (dirToDirPlusPackageName.Contains(dirName))
            {
                var mapped = Path.Combine(outputPath, dirName);
                var mapped2 = Path.Combine(mapped, package.FullName);
                Fs.Directory.CreateDirectory(mapped);
                MoveOrMergeOverwrite(dirPath, mapped2);
                continue;
            }

            if (dirToDir.Contains(dirName))
            {
                var mapped = Path.Combine(outputPath, dirName);
                MoveOrMergeOverwrite(dirPath, mapped);
                continue;
            }

            MapRecursive(package, dirPath, outputPath);
        }

        // Flatten the rest.
        foreach (var fileDir in Fs.Directory.EnumerateFiles(directoryPath).AsValueEnumerable())
        {
            var fileName = Path.GetFileName(fileDir);

            string dest;
            // Special cases
            if (fileName.EndsWith(".mm.dll", StringComparison.OrdinalIgnoreCase))
            {
                dest = Path.Combine(outputPath, "monomod", package.FullName, fileName);
                Fs.Directory.CreateDirectory(dest);
            }
            else
                dest = Path.Combine(outputPath, defaultDir, package.FullName, fileName);

            if (Fs.File.Exists(dest))
            {
                Fs.File.Delete(dest);
            }
            Fs.File.Move(fileDir, dest);
        }
    }

    void MoveOrMergeOverwrite(string sourceDirName, string destDirName)
    {
        if (!Fs.Directory.Exists(destDirName))
        {
            Fs.Directory.Move(sourceDirName, destDirName);
            return;
        }

        foreach (var file in Fs.Directory.EnumerateFiles(sourceDirName).AsValueEnumerable())
        {
            var fileName = Path.GetFileName(file);
            var dest = Path.Combine(destDirName, fileName);
            if (Fs.File.Exists(dest))
            {
                Fs.File.Delete(dest);
            }
            Fs.File.Move(file, dest);
        }

        foreach (var dir in Fs.Directory.EnumerateDirectories(sourceDirName).AsValueEnumerable())
        {
            var dirName = Path.GetFileName(dir);
            MoveOrMergeOverwrite(dir, Path.Combine(destDirName, dirName));
        }

        Fs.Directory.Delete(sourceDirName);
    }

    public async Task<bool> InstallPackageAsync(
        VisualPackageVersion packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var path = await packageVersion.ExtractAsync(cancellationToken);
        if (path is null)
        {
            Cog.Error($"Cannot install package which is not downloaded: '{packageVersion}'");
            return false;
        }

        string installRoot = GetInstallRoot(packageVersion, profileFilesDirectory);
        Directory.CreateDirectory(installRoot);
        var pathCopy = path + ".temp";

        Fs.Directory.CreateDirectory(pathCopy);
        CopyDirectory(path, pathCopy);
        Map(packageVersion, pathCopy, installRoot);

        return true;
    }

    private static string GetInstallRoot(
        VisualPackageVersion packageVersion,
        string profileFilesDirectory
    )
    {
        if (IsBepInExPackage(packageVersion))
        {
            return profileFilesDirectory;
        }

        return Path.Combine(profileFilesDirectory, "BepInEx");
    }

    public async Task<bool> UninstallPackageAsync(
        VisualPackageVersion packageVersion,
        string profileFilesDirectory,
        CancellationToken cancellationToken = default
    )
    {
        var path = await packageVersion.ExtractAsync(cancellationToken);
        if (path is null)
        {
            Cog.Error($"Cannot uninstall package which is not downloaded: '{packageVersion}'");
            return false;
        }

        // We Map the extracted directory for our modloader to the actual final directory,
        // except in a fake filesystem to avoid actually moving our files.
        var fakeFs = new MockFileSystem(
            IModInstallRules
                .RealFileSystem.Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    keySelector: x => x,
                    elementSelector: x => new MockFileData(string.Empty)
                )
        );

        var fakeInstallRules = new BepInExModInstallRules(fakeFs);
        string installRoot = GetInstallRoot(packageVersion, profileFilesDirectory);

        fakeInstallRules.Map(packageVersion, path, installRoot);

        // We don't want users' config files to be deleted if a package ships
        // config files and the package is uninstalled. It's possible the user
        // might want to install the package again and we'd like to keep its configs
        // like any other package which doesn't ship its config file.
        var configDir = Path.Combine(profileFilesDirectory, "BepInEx", "config");
        if (fakeFs.Directory.Exists(configDir))
        {
            fakeFs.Directory.Delete(configDir, recursive: true);
        }

        // Then we just delete the fake mapped files from our real filesystem.
        DeleteDirectoryContentsBasedOnSource(fakeFs, installRoot);

        return true;
    }

    void CopyDirectory(string sourceDirName, string destDirName)
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

    public List<string> GetLaunchArguments(LazyModList modList)
    {
        var isWindowsApp = !modList.IsLinuxNative();

        var profileFiles = modList.ProfileFilesDirectory;
        var gamePath = modList.GetGamePathOrThrow();
        var executables = Directory
            .GetFiles(gamePath)
            .AsValueEnumerable()
            .Where(x =>
            {
                var ext = Path.GetExtension(x);

                if (isWindowsApp)
                {
                    if (ext is ".exe" && Path.GetFileName(x) is not "UnityCrashHandler64.exe")
                        return true;

                    return false;
                }

                if (ext is ".x86_64" or ".x86")
                    return true;

                if (Path.GetFileName(x) == Path.GetFileName(gamePath))
                    return true;

                return false;
            })
            .ToArray();

        if (executables.Length > 1)
        {
            throw new FileNotFoundException(
                $"Too many executable candidates: '{string.Join("', '", executables)}'"
            );
        }
        else if (executables.Length == 0)
        {
            throw new FileNotFoundException($"No exes found at '{gamePath}'");
        }

        var gameExecutable = executables[0];

        List<string> args = [];

        if (!isWindowsApp)
        {
            args.Add(Path.Combine(profileFiles, "run_bepinex.sh"));
        }
        args.Add(gameExecutable);
        args.Add("--doorstop-target-assembly");
        args.Add(Path.Combine(profileFiles, "BepInEx", "core", "BepInEx.Preloader.dll"));

        return args;
    }
}
