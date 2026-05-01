global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using MemoryPack;
using MemoryPack.Compression;
using Serilog;
using Serilog.Core;
using ZLinq;

namespace Cogwork.Core;

public static class CogworkCoreLogger
{
#pragma warning disable IDE0052 // Private member can be removed as the value assigned to it is never read
    static readonly Finalizer finalizer = new();
#pragma warning restore

    sealed class Finalizer
    {
        ~Finalizer()
        {
            LogMutex.ReleaseMutex();
            LogMutex.Dispose();
        }
    }

    static CogworkCoreLogger()
    {
        var assembly = typeof(CogworkCoreLogger).Assembly;
        var name = assembly.GetName().Name;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        string mutexId = $@"Global\Hamunii.Cogwork.Logger";
        int numId = 0;
        bool createdNew = false;
        while (true)
        {
            Mutex mutex = new(true, mutexId + numId, out createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                numId++;
            }
            else
            {
                LogMutex = mutex;
                break;
            }
        }

        LogFileLocation = Path.Combine(
            CogworkPaths.GetCacheSubDirectory("logs"),
            $"instance-{numId}-date-.log"
        );

        Cog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
#if DEBUG
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning,
#else
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error,
#endif
                formatProvider: CultureInfo.InvariantCulture
            )
            .WriteTo.File(
                LogFileLocation,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 2,
                buffered: false
            )
            .CreateLogger();

        Cog.Debug($"=============================");
        Cog.Debug($"{name} {version} initialized.");
    }

    public static Logger Cog { get; }
    static string LogFileLocation { get; }
    static Mutex LogMutex { get; }
}

public interface IPackageSourceService
{
    public string PackageIndexBaseDirectory { get; }
    public string PackageIndexCacheLocation { get; }

    /// <summary>
    /// A subdirectory where all packages from this source are installed to.<br/>
    /// Packages are not installed into profiles directly.
    /// </summary>
    public string PackageInstallSubDirectory { get; }
    public Uri Uri { get; }
    public string Id { get; }
    public Game Game { get; }

    public bool IsIncompleteIndexCache();

    public Task<bool> FetchIndexToCacheAsync(ProgressContext progress = default);

    public bool IsPackageDownloaded(VisualPackageVersion packageVersion);

    public Task<string?> ExtractAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    );

    public Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    );

    // Apparently one should preferably keep a singleton of HttpClient.
    internal static HttpClient SharedClient { get; } = new();

    public readonly record struct PerformedOrNot<T>(
        [property: MemberNotNullWhen(true, nameof(PerformedOrNot<>.Value))] bool Performed,
        T? Value = default
    );

    /// <summary>
    /// Performs a task if it's not actively being
    /// performed by another thread or application instance,
    /// otherwise waits until the preexisting task is finished.
    /// </summary>
    public static async Task<PerformedOrNot<T>> DoTaskOrWaitForCompletionAsync<T>(
        string lockDirectory,
        ProgressContext progress,
        Func<ProgressContext, Task<T>> doTask
    )
    {
        if (!Directory.Exists(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        bool waitedForLock = false;
        while (true)
        {
            try
            {
                using FileStream lockFile = new(
                    Path.Combine(lockDirectory, ".lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.Read,
                    FileShare.None
                );

                if (waitedForLock)
                {
                    Cog.Warning($"Got lock for '{lockDirectory}'");
                    return new(false);
                }
                return new(true, await doTask(progress));
            }
            catch (IOException)
            {
                if (!waitedForLock)
                {
                    waitedForLock = true;
                    Cog.Warning($"Awaiting lock for '{lockDirectory}' instead of fetching");
                }
                await Task.Delay(100);
            }
        }
    }
}

public sealed class ThunderstoreCommunity(Game game) : IPackageSourceService
{
    [JsonIgnore]
    public string PackageIndexBaseDirectory =>
        field ??= CogworkPaths.GetCacheIndexSubDirectory(game.Slug, "thunderstore");

    public string PackageIndexIndexDirectory =>
        field ??= CogworkPaths.CombineAndCreate(PackageIndexBaseDirectory, "index");

    public string PackageInstallSubDirectory { get; } = "thunderstore";

    public Uri Uri { get; } = new($"https://thunderstore.io/c/{game.Slug}/");
    public string Id => field ??= Uri.ToString();
    public Game Game => game;

    public string PackageIndexCacheLocation =>
        field ??= Path.Combine(PackageIndexBaseDirectory, $"index-cache.json");

    readonly Lock _totalBytesLock = new();
    readonly Lock _totalContentLengthLock = new();

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexIndexDirectory)
            .Any(x => x.EndsWith(".todo", StringComparison.Ordinal));

    public string PackageIndexLocation(string hash) =>
        Path.Combine(PackageIndexIndexDirectory, hash);

    public async Task<bool> FetchIndexToCacheAsync(ProgressContext progress = default)
    {
        var result = await IPackageSourceService.DoTaskOrWaitForCompletionAsync(
            PackageIndexBaseDirectory,
            progress,
            DoIndexFetchLogicAsync
        );

        if (!result.Performed)
            return true;

        return result.Value;
    }

    async Task<bool> DoIndexFetchLogicAsync(ProgressContext progress)
    {
        var url = $"https://thunderstore.io/c/{game.Slug}/api/v1/package-listing-index/";

        Cog.Information("Fetching: " + url);

        HttpClient client = IPackageSourceService.SharedClient;
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Cog.Error("Error fetching url for package index: " + response.StatusCode);
            return false;
        }

        (string url, string fileName)[] allPackageIndexUrls;
        (string url, string fileName)[] newPackageIndexUrls;
        {
            using GZipStream zipStream = new(
                response.Content.ReadAsStream(),
                CompressionMode.Decompress
            );
            var strings = JsonSerializer.Deserialize(zipStream, JsonGen.Default.StringArray);
            if (strings is null)
            {
                Cog.Error($"Expected string[] but received null from '{url}'.");
                return false;
            }
            allPackageIndexUrls = [.. strings.Select(url => (url, url.Split('/')[^1]))];
            newPackageIndexUrls =
            [
                .. allPackageIndexUrls.Where(x => !File.Exists(PackageIndexLocation(x.fileName))),
            ];
            Cog.Debug(
                $"Got package index urls: {newPackageIndexUrls.Length} new, {strings.Length} total"
            );
        }

        Cog.Debug(
            $"Downloading {newPackageIndexUrls.Length} package indexes to {PackageIndexIndexDirectory}"
        );

        var progresses = new double[newPackageIndexUrls.Length];
        double combinedTotalBytes = 0;
        long totalContentLength = 0;
        int i = 0;
        var tasks = newPackageIndexUrls
            .Select(
                async Task<HttpStatusCode> (x) =>
                {
                    using var zipFileStream = new FileStream(
                        PackageIndexLocation(x.fileName) + ".todo",
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None
                    );

                    ProgressContext progressContext = progress;
                    if (progress.Progress is { } combinedProgress)
                    {
                        var j = i;
                        // Cog.Debug($"progress[{j}] is initializing");
                        var progressCombinator = new Progress<double>(totalBytes =>
                        {
                            var diff = totalBytes - progresses[j];
                            progresses[j] = totalBytes;
                            double combinedTotalBytesCopy;
                            lock (_totalBytesLock)
                            {
                                combinedTotalBytesCopy = combinedTotalBytes += diff;
                            }
                            // Cog.Debug(
                            //     $"progress[{j}] diff: {diff}, totalBytes: {combinedTotalBytesCopy}"
                            // );
                            combinedProgress.Report(combinedTotalBytes);
                        });

                        progressContext = new(
                            progressCombinator,
                            (p, contentLength) =>
                            {
                                lock (_totalContentLengthLock)
                                {
                                    var oldContentLength = totalContentLength;
                                    totalContentLength += (long)contentLength!;
                                    // Cog.Debug(
                                    //     $"progress[{j}] updated totalContentLength to {totalContentLength} from {oldContentLength}"
                                    // );
                                    progress.OnContentLengthKnown!(
                                        combinedProgress,
                                        totalContentLength
                                    );
                                }
                            }
                        );
                    }
                    i++;
                    var status = await client.DownloadAsync(x.url, zipFileStream, progressContext);
                    Cog.Debug($"Downloaded index {x.fileName}");
                    return status;
                }
            )
            .ToArray();

        var statuses = await Task.WhenAll(tasks);

        int j = 0;
        foreach (var status in statuses)
        {
            if (!status.IsSuccess)
            {
                Cog.Error("Error fetching package index url: " + status);
                return false;
            }
            var packageIndexLocation = PackageIndexLocation(newPackageIndexUrls[j].fileName);
            File.Move(packageIndexLocation + ".todo", packageIndexLocation, overwrite: true);
            j++;
        }

        bool anyOutdated = false;
        var allUpToDateFiles = allPackageIndexUrls.Select(x => x.fileName).ToArray();
        foreach (
            var outdated in Directory
                .EnumerateFiles(PackageIndexIndexDirectory)
                .Where(x => !allUpToDateFiles.Contains(Path.GetFileName(x)))
        )
        {
            anyOutdated = true;
            Cog.Debug($"Deleting outdated cache file '{outdated}'");
            File.Delete(outdated);
        }

        if (anyOutdated || newPackageIndexUrls.Length > 0)
        {
            var optimizedPath = Path.Combine(PackageIndexBaseDirectory, "optimized");
            if (Directory.Exists(optimizedPath))
            {
                Directory.Delete(optimizedPath, recursive: true);
            }
        }

        Cog.Information("Fetched successfully.");
        return true;
    }

    public bool IsPackageDownloaded(VisualPackageVersion packageVersion) =>
        IsPackageDownloaded(packageVersion, out _, out _, out _);

    bool IsPackageDownloaded(
        VisualPackageVersion packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        var version = packageVersion.Version.ToString();
        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            PackageInstallSubDirectory,
            packageVersion.FullName
        );

        zipFileLocation = Path.Combine(installPathRoot, $"{version}.zip");
        directoryPath = Path.Combine(installPathRoot, version, "files");

        var dirExists = Directory.Exists(directoryPath);
        zipExists = File.Exists(zipFileLocation);
        return dirExists || zipExists;
    }

    public async Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        if (
            IsPackageDownloaded(
                (VisualPackageVersion)packageVersion,
                out string? zipFileLocation,
                out _,
                out _
            )
        )
        {
            Cog.Debug($"Package is already downloaded for '{packageVersion}'");
            return true;
        }

        var package = packageVersion.Package;
        var author = package.Author.Name;
        var name = package.Name;
        var version = packageVersion.Version.ToString();
        var downloadUrl = $"https://thunderstore.io/package/download/{author}/{name}/{version}/";
        Cog.Debug($"Attempting to download: {downloadUrl}");

        var inProgressLocation = zipFileLocation + ".todo";
        {
            using var fileStream = new FileStream(
                inProgressLocation,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );

            HttpClient client = IPackageSourceService.SharedClient;
            var statusCode = await client.DownloadAsync(
                downloadUrl,
                fileStream,
                progress,
                cancellationToken
            );

            if (!statusCode.IsSuccess)
            {
                Cog.Error($"Error downloading package '{packageVersion}': " + statusCode);
                return false;
            }
        }
        File.Move(inProgressLocation, zipFileLocation);

        Cog.Debug($"Download complete for: {downloadUrl}");
        return true;
    }

    public async Task<string?> ExtractAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !IsPackageDownloaded(
                packageVersion,
                out var zipPath,
                out var directoryPath,
                out var zipExists
            )
        )
        {
            Cog.Error($"Cannot extract package which is not downloaded: '{packageVersion}'");
            return null;
        }

        if (zipExists is false)
        {
            // already extracted
            return directoryPath;
        }

        var tempDirPath = directoryPath + ".temp";

        if (Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);

        if (Directory.Exists(tempDirPath))
            Directory.Delete(tempDirPath, recursive: true);
        {
            using FileStream fileStream = File.Open(zipPath, FileMode.Open);
            await ZipFile.ExtractToDirectoryAsync(fileStream, directoryPath, cancellationToken);
        }

        File.Delete(zipPath);
        return directoryPath;
    }
}

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

public sealed class PackageSourceIndex
{
    /// <summary>
    /// The package source which is resolved when a package source is not defined.
    /// This should be Thunderstore, if Thunderstore is present.
    /// </summary>
    [JsonIgnore]
    public PackageSource? Thunderstore { get; private set; }

    [JsonIgnore]
    public ReadOnlyCollection<PackageSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<PackageSource> PackageSources { get; } = [];

    public PackageSourceIndex() { }

    public PackageSourceIndex(PackageSource packageSource)
    {
        Add(packageSource);
    }

    public PackageSourceIndex(IEnumerable<Uri> uris) => Import(uris);

    public void Import(IEnumerable<Uri> uris)
    {
        foreach (var uri in uris.AsValueEnumerable())
        {
            if (!PackageSources.Any(x => uri == x.Service.Uri))
            {
                if (TryParseFromUri(uri, out var packageSource))
                {
                    Add(packageSource);
                }
                else
                {
                    Cog.Warning($"Could not parse package source uri: '{uri}'");
                }
            }
        }
    }

    public static bool TryParseFromUri(Uri uri, [NotNullWhen(true)] out PackageSource? source)
    {
        source = default;

        switch (uri.Scheme)
        {
            case "test":
                source = new(new TestPackageSource());
                return true;
        }

        switch (uri.Authority)
        {
            case "thunderstore.io":
                var span = uri.AbsolutePath.AsSpan();
                var split = span.Split('/');

                split.MoveNext(); // skip /
                split.MoveNext(); // skip c
                split.MoveNext();

                var slug = span[split.Current];
                Cog.Verbose($"slug from uri: {slug} | {uri.AbsolutePath} | {uri}");

                var nameToGame = Game.NameToGame.GetAlternateLookup<ReadOnlySpan<char>>();
                if (nameToGame.TryGetValue(slug, out var game))
                {
                    source = new(new ThunderstoreCommunity(game));
                    return true;
                }

                Cog.Warning($"Couldn't find game by name '{slug}'");
                break;
        }

        return false;
    }

    public void Add(PackageSource packageSource)
    {
        PackageSources.Add(packageSource);
        packageSource.SourceIndex = this;

        if (Thunderstore is null && packageSource.Service is ThunderstoreCommunity)
        {
            Thunderstore = packageSource;
        }
    }

    public async Task<IEnumerable<Package>> GetAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Information($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources.Select(x => x.GetPackagesAsync(progressFactory)).ToArray();
#if DEBUG
        // Simulate at least some delay to make sure things are awaited properly.
        await Task.Delay(100);
#endif
        Task.WaitAll(fetchTasks);
        return fetchTasks.SelectMany(x => x.Result);
    }

    public async Task FetchAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.FetchPackageIndexAutomaticAsync(progressFactory))
            .ToArray();
#if DEBUG
        await Task.Delay(100);
#endif
        await Task.WhenAll(fetchTasks);
    }

    public async Task FetchAllPackagesManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.FetchPackageIndexManualAsync(progressFactory))
            .ToArray();
#if DEBUG
        await Task.Delay(100);
#endif
        await Task.WhenAll(fetchTasks);
    }
}

public sealed class PackageSource
{
    public sealed class PackageSourceCache : ISaveWithJson
    {
        public DateTime LastFetch { get; set; }
    }

    public static PackageSource ThunderstoreSilksong { get; } =
        new(new ThunderstoreCommunity(Game.Silksong));

    public static PackageSourceIndex Silksong { get; } = new(ThunderstoreSilksong);

    public static TimeSpan TimeUntilAutomaticIndexRefresh { get; } = TimeSpan.FromMinutes(20);
    public static TimeSpan TimeUntilManualIndexRefreshAllowed { get; } = TimeSpan.FromSeconds(10);

    internal PackageSourceCache SourceCache =>
        field ??= PackageSourceCache.LoadSavedDataOrNew(
            Service.PackageIndexCacheLocation,
            JsonGen.Default.PackageSourceCache
        );

    public PackageSourceIndex SourceIndex { get; internal set; } = null!;
    public IPackageSourceService Service { get; private set; }
    private List<Package> Packages { get; set; } = [];

    internal ConcurrentDictionary<string, Package> nameToPackage = [];
    bool isImported;

    public PackageSource(IPackageSourceService service)
    {
        Service = service;
    }

    public async Task FetchPackageIndexAutomaticAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _ = await FetchPackageIndexAsync(TimeUntilAutomaticIndexRefresh, progressFactory);
    }

    public async Task FetchPackageIndexManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _ = await FetchPackageIndexAsync(TimeUntilManualIndexRefreshAllowed, progressFactory);
    }

    private async Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory
    )
    {
        var dateNow = DateTime.Now;
        var lastFetch = SourceCache.LastFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.Add(timeUntilIndexRefreshAllowed))
            fetchAgain = true;

        if (fetchAgain || Service.IsIncompleteIndexCache())
        {
            var progress = progressFactory?.Invoke(this) ?? default;

            var successfulFetch = await Service.FetchIndexToCacheAsync(progress);
            if (!successfulFetch)
                return false;

            SourceCache.LastFetch = dateNow;
            SourceCache.Save(Service.PackageIndexCacheLocation, JsonGen.Default.PackageSourceCache);
        }
        else
        {
            Cog.Information(
                $"Using cached package index for '{Service.Uri}', last fetch was "
                    + $"less than {timeUntilIndexRefreshAllowed} ago."
            );

            if (isImported)
            {
                return true;
            }
        }

        var packages = await ParsePackageIndexAsync(Service.PackageIndexBaseDirectory);
        if (packages is null)
        {
            Cog.Error("Package index parsing failed");
            return false;
        }
        Packages = packages;
        isImported = true;
        return true;
    }

    public async Task<List<Package>> GetPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        await FetchPackageIndexAutomaticAsync(progressFactory);
        return Packages;
    }

    Lock _lock = new();

    internal async Task<List<Package>?> ParsePackageIndexAsync(string packageIndexBasePath)
    {
        var optimizedPath = Path.Combine(packageIndexBasePath, "optimized");

        if (Directory.Exists(optimizedPath))
        {
            Cog.Debug($"Deserializing fast index '{optimizedPath}'...");

            List<Package> allPackages2 = [];

            var result2 = Parallel.ForEach(
                Directory.EnumerateFiles(optimizedPath),
                (indexFile, state) =>
                {
                    try
                    {
                        using var decompressor = new BrotliDecompressor();
                        byte[] bytes = File.ReadAllBytes(indexFile);
                        var decompressed = decompressor.Decompress(bytes);

                        var packages = ParsePackageIndexMemoryPackAsync(indexFile, decompressed);

                        if (packages is null)
                        {
                            state.Break();
                            return;
                        }

                        lock (allPackages2)
                        {
                            allPackages2.AddRange(packages.Values);
                        }
                    }
                    catch (Exception ex)
                    {
                        Cog.Error(indexFile + ": " + ex.ToString());
                        return;
                    }
                }
            );

            if (!result2.IsCompleted)
            {
                return default;
            }

            Cog.Debug($"Deserialized fast index");

            return allPackages2;
        }

        var packageIndexPath = Path.Combine(packageIndexBasePath, "index");

        if (!Directory.Exists(packageIndexPath))
        {
            Cog.Error($"Package index directory '{packageIndexPath}' must exist.");
            return default;
        }

        Cog.Debug($"Loading JSON index '{packageIndexPath}'...");

        List<Package> allPackages = [];

        var result = Parallel.ForEach(
            Directory.EnumerateFiles(packageIndexPath),
            (indexFile, state) =>
            {
                try
                {
                    using var fileStream = new FileStream(
                        indexFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    );
                    using GZipStream zipStream = new(fileStream, CompressionMode.Decompress);
                    if (!TryParsePackageIndexJson(indexFile, zipStream, out var packages1))
                    {
                        state.Break();
                        return;
                    }

                    lock (allPackages)
                    {
                        allPackages.AddRange(packages1);
                    }
                }
                catch (Exception ex)
                {
                    Cog.Error(indexFile + ": " + ex.ToString());
                    return;
                }
            }
        );

        if (!result.IsCompleted)
        {
            return default;
        }

        Cog.Debug($"Loaded JSON index");

        Cog.Debug($"Serializing fast index to '{optimizedPath}'");
        Directory.CreateDirectory(optimizedPath);

        int i = 0;
        await Task.WhenAll(
            allPackages
                .Chunk(2000)
                .Select(chunk => CompressAndWriteChunk(optimizedPath, i++, chunk))
        );

        Cog.Debug("Serialized fast index");

        return allPackages;
    }

    private static async Task CompressAndWriteChunk(string optimizedPath, int i, Package[] chunk)
    {
        var list = new PackageList();
        list.Values.AddRange(chunk);
        using var compressor = new BrotliCompressor();
        MemoryPackSerializer.Serialize(compressor, list);
        using var fileStream = File.OpenWrite(Path.Combine(optimizedPath, $"{i}.bin"));
        await compressor.CopyToAsync(fileStream);
    }

    internal PackageList? ParsePackageIndexMemoryPackAsync(
        string fileName,
        ReadOnlySequence<byte> bytes
    )
    {
        try
        {
            var packages = MemoryPackSerializer.Deserialize<PackageList>(bytes);
            if (packages is null)
            {
                Cog.Error($"Package index file '{fileName}' deserialization returned null");
                return default;
            }
            ProcessPackages(packages.Values);
            return packages;
        }
        catch (Exception ex)
        {
            Cog.Error($"Error reading package index file '{fileName}': {ex}");
            return default;
        }
    }

    internal bool TryParsePackageIndexJson(
        string fileName,
        Stream data,
        [NotNullWhen(true)] out List<Package>? packages
    )
    {
        try
        {
            packages = JsonSerializer.Deserialize(data, JsonGen.Default.ListPackage);
            if (packages is null)
            {
                Cog.Error($"Package index file '{fileName}' deserialization returned null");
                return false;
            }
            ProcessPackages(packages);
            return true;
        }
        catch (JsonException ex)
        {
            byte[] buffer = new byte[100];
            data.Position = 0;
            _ = data.Read(buffer);
            var beginning = Encoding.UTF8.GetString(buffer);
            Cog.Error(
                $"Error reading package index file '{fileName}' with contents beginning with: '{beginning}'\n"
                    + "And error: "
                    + ex.ToString()
            );
            packages = default;
            return false;
        }
    }

    private void ProcessPackages(List<Package> packages)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            Package package = packages[i];
            package.Source = this;

            if (
                !nameToPackage
                    .GetAlternateLookup<ReadOnlySpan<char>>()
                    .TryAdd(package.FullName, package)
            )
            {
                // If we are here, we just created a whole lot of
                // duplicate package instances. We connect the instances:
                if (
                    Package.TryGetPackage(
                        SourceIndex,
                        package.FullName,
                        out var oldPackage,
                        false,
                        out _,
                        out _
                    )
                )
                {
                    oldPackage.Versions = package.Versions;
                    packages[i] = oldPackage;
                }
            }
        }
    }

    public override string ToString()
    {
        var url = Service.Uri;
        return url.ToString();
    }
}
