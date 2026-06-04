using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core;

public sealed class LocalPackageSource : PackageSource
{
    public static LocalPackageSource Instance { get; } = new();

    public override Uri Uri { get; } = new("cogman:sources/local");

    public override string Id => field ??= Uri.ToString();

    public string PackageIndexPath { get; } =
        Path.Combine(CogworkPaths.GetPackagesSubDirectory("local"), "local-index.json");

    public override bool IsPackageDownloaded(
        VisualPackageVersion packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        // For local packages, it might be best if only one version is allowed?
        // This might prevent accidentally using outdated versions, which might
        // also not even match the actual packages uploaded to e.g. Thunderstore.
        // var version = packageVersion.Version.ToString();
        var version = "latest";

        return IsPackageDownloaded(
            packageVersion,
            withVersionName: version,
            out zipFileLocation,
            out directoryPath,
            out zipExists
        );
    }

    public static bool IsPackageDownloaded(
        VisualPackageVersion packageVersion,
        string withVersionName,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        var version = withVersionName;

        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            "local",
            packageVersion.FullName
        );

        zipFileLocation = Path.Combine(installPathRoot, $"{version}.zip");
        directoryPath = Path.Combine(installPathRoot, version, "files");

        var dirExists = Directory.Exists(directoryPath);
        zipExists = File.Exists(zipFileLocation);
        return dirExists || zipExists;
    }

    public override Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsPackageDownloaded((VisualPackageVersion)packageVersion))
        {
            Cog.Error(
                $"Attempting to download local package '{packageVersion}' which is not found."
            );
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public override async Task<string?> ExtractAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        // TODO: For now, the package must be uninstalled from all profiles it has been installed to.
        // This is because the package installation is the source of truth for which files belong to
        // the package.
        // It would be more optimal if each profile tracked each file added by each installed package.
        // The following code is rather horrible, and I want to get rid of it.
        if (
            IsPackageDownloaded(
                packageVersion,
                withVersionName: "next",
                out var zipPathTemp,
                out var directoryPathTemp,
                out var zipExistsTemp
            )
        )
        {
            foreach (var game in Game.SupportedGames)
            {
                foreach (var profile in game.EnumerateProfiles())
                {
                    var localPackage = profile
                        .GetResolved()
                        .FirstOrDefault(x => x.FullName == packageVersion.FullName);

                    if (!IsLocalSource(localPackage.Source))
                        continue;

                    var userPackages = InstalledPackages
                        .LoadSavedData(profile.ProfileInstalledPackagesFilePath)
                        .UserPackages;

                    var isUninstalled = await profile.Game.InstallRules.UninstallPackageAsync(
                        packageVersion,
                        profile.ProfileFilesDirectory,
                        cancellationToken
                    );

                    if (isUninstalled)
                    {
                        if (userPackages is { })
                        {
                            var installed = new InstalledPackages(
                                userPackages
                                    .Where(x =>
                                        x.PackageVersion.FullName != packageVersion.FullName
                                    )
                                    .Append(new UserPackage(packageVersion, IsInstalled: false))
                                    .ToArray()
                            );
                            installed.Save(profile.ProfileInstalledPackagesFilePath);
                        }

                        Cog.Debug(
                            $"Uninstalled local old version of '{packageVersion}'"
                                + $" for profile '{profile.DisplayName}' for game '{game.Slug}'"
                        );
                    }
                    else
                    {
                        Cog.Error(
                            $"Failed to uninstall local package '{packageVersion}'"
                                + $" for profile '{profile.DisplayName}' for game '{game.Slug}'"
                        );
                    }
                }
            }

            _ = IsPackageDownloaded(
                packageVersion,
                out var zipPathFinal,
                out var directoryPathFinal,
                out var zipExistsFinal
            );

            if (Directory.Exists(directoryPathFinal))
            {
                Directory.Delete(directoryPathFinal, recursive: true);
                Directory.Move(directoryPathTemp, directoryPathFinal);
            }

            if (Directory.Exists(directoryPathTemp))
            {
                Directory.Delete(directoryPathTemp, recursive: true);
            }

            if (zipExistsTemp)
            {
                if (zipExistsFinal)
                    File.Delete(zipPathFinal);

                File.Move(zipPathTemp, zipPathFinal);
            }
        }
        return await base.ExtractAsync(packageVersion, cancellationToken);
    }

    bool IsLocalSource(string? source)
    {
        return source == Id;
    }

    public override async Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory
    )
    {
        if (!File.Exists(PackageIndexPath))
        {
            Packages ??= [];
            Cog.Debug("Fetched local package index (which has not been created yet)");
            return true;
        }

        using var fileStream = File.Open(PackageIndexPath, FileMode.Open);
        var packages = JsonSerializer.Deserialize(fileStream, JsonGen.Default.ListPackage);
        if (packages is null)
        {
            Packages ??= [];
            Cog.Error($"Package index file '{PackageIndexPath}' deserialization returned null");
            return false;
        }
        ProcessPackages(packages);
        Packages = packages;

        Cog.Debug("Fetched local package index");
        return true;
    }

    public string? ImportPackage(string path)
    {
        Cog.Information($"Importing local package at '{path}'");

        var manifest = Path.Combine(path, "manifest.json");

        if (File.Exists(path))
        {
            using FileStream fileStream = File.Open(path, FileMode.Open);
            var archive = new ZipArchive(fileStream);

            var manifestFile = archive.Entries.FirstOrDefault(x =>
                x.FullName.Equals("manifest.json", StringComparison.Ordinal)
            );
            if (manifestFile is null)
            {
                return "Manifest file not found in archive";
            }

            using var manifestStream = manifestFile.Open();
            return ImportPackageFromManifest(
                path,
                manifest,
                manifestStream,
                () =>
                {
                    manifestStream.Dispose();
                    archive.Dispose();
                    fileStream.Dispose();
                }
            );
        }
        else if (!File.Exists(manifest))
        {
            throw new FileNotFoundException($"Manifest not found: '{manifest}'");
        }
        else
        {
            using var manifestStream = File.OpenRead(manifest);
            return ImportPackageFromManifest(path, manifest, manifestStream);
        }
    }

    private string? ImportPackageFromManifest(
        string path,
        string manifestPath,
        Stream manifestStream,
        Action? disposeStreams = null
    )
    {
        var packageVersion = JsonSerializer.Deserialize(
            manifestStream,
            JsonGen.Default.PackageVersion
        );

        disposeStreams?.Invoke();

        if (packageVersion is null)
        {
            return $"Package manifest file '{manifestPath}' deserialization returned null";
        }

        _ = FetchPackageIndexAsync(TimeSpan.Zero, default).Result;

        var package = new Package(packageVersion.Author, packageVersion.Name, [packageVersion]);

        // Overwrites existing package
        ProcessPackage(package);

        if (
            IsPackageDownloaded(
                (VisualPackageVersion)packageVersion,
                withVersionName: "next",
                out var zipFileLocation,
                out var directoryPath,
                out var zipExists
            )
        )
        {
            if (zipExists)
            {
                File.Delete(zipFileLocation);
            }

            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }

        if (File.Exists(path))
        {
            File.Copy(path, zipFileLocation);
        }
        else
        {
            Utils.CopyDirectory(path, directoryPath, recursive: true);
        }

        Packages = [.. nameToPackage.Select(x => x.Value)];
        var localPackageIndex = JsonSerializer.Serialize(Packages, JsonGen.Default.ListPackage);
        File.WriteAllText(PackageIndexPath, localPackageIndex);

        return null;
    }
}

public sealed class ThunderstoreCommunity(Game game) : PackageSource
{
    [JsonIgnore]
    public string PackageIndexBaseDirectory =>
        field ??= CogworkPaths.GetCacheIndexSubDirectory(game.Slug, "thunderstore");

    public string PackageIndexIndexDirectory =>
        field ??= CogworkPaths.CombineAndCreate(PackageIndexBaseDirectory, "index");

    public string PackageInstallSubDirectory { get; } = "thunderstore";

    public override Uri Uri { get; } = new($"https://thunderstore.io/c/{game.Slug}/");
    public override string Id => field ??= Uri.ToString();

    public string PackageIndexCacheLocation =>
        field ??= Path.Combine(PackageIndexBaseDirectory, $"index-cache.json");

    internal PackageSourceCache SourceCache =>
        field ??= PackageSourceCache.LoadSavedDataOrNew(
            PackageIndexCacheLocation,
            JsonGen.Default.PackageSourceCache
        );

    readonly Lock _totalBytesLock = new();
    readonly Lock _totalContentLengthLock = new();
    bool isImported;

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexIndexDirectory)
            .Any(x => x.EndsWith(".todo", StringComparison.Ordinal));

    public string PackageIndexLocation(string hash) =>
        Path.Combine(PackageIndexIndexDirectory, hash);

    public override async Task<bool> FetchPackageIndexAsync(
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

        if (fetchAgain || IsIncompleteIndexCache())
        {
            var progress = progressFactory?.Invoke(this) ?? default;

            var successfulFetch = await FetchIndexToCacheAsync(progress);
            if (!successfulFetch)
                return false;

            SourceCache.LastFetch = dateNow;
            SourceCache.Save(PackageIndexCacheLocation, JsonGen.Default.PackageSourceCache);
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

        var packages = await ParsePackageIndexAsync(PackageIndexBaseDirectory);
        if (packages is null)
        {
            Cog.Error("Package index parsing failed");
            return false;
        }
        Packages = packages;
        isImported = true;
        return true;
    }

    public async Task<bool> FetchIndexToCacheAsync(ProgressContext progress = default)
    {
        var result = await Utils.DoTaskOrWaitForCompletionAsync(
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

        HttpClient client = Utils.SharedHttpClient;
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

        var allUpToDateFiles = allPackageIndexUrls.Select(x => x.fileName).ToArray();
        foreach (
            var outdated in Directory
                .EnumerateFiles(PackageIndexIndexDirectory)
                .Where(x => !allUpToDateFiles.Contains(Path.GetFileName(x)))
        )
        {
            Cog.Debug($"Deleting outdated cache file '{outdated}'");
            File.Delete(outdated);
        }

        Cog.Information("Fetched successfully.");
        return true;
    }

    public override bool IsPackageDownloaded(
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

    public override async Task<bool> DownloadPackageAsync(
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

            HttpClient client = Utils.SharedHttpClient;
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
}

public abstract class PackageSource
{
    public sealed class PackageSourceCache : ISaveWithJson
    {
        public DateTime LastFetch { get; set; }
    }

    public PackageSourceIndex SourceIndex { get; internal set; } = null!;
    public PackageSource Service => this;
    protected List<Package> Packages { get; set; } = [];
    public abstract Uri Uri { get; }
    public abstract string Id { get; }

    internal ConcurrentDictionary<string, Package> nameToPackage = [];

    public async Task FetchPackageIndexAutomaticAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _ = await FetchPackageIndexAsync(TimeSpan.FromMinutes(20), progressFactory);
    }

    public async Task FetchPackageIndexManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _ = await FetchPackageIndexAsync(TimeSpan.FromSeconds(10), progressFactory);
    }

    public abstract Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory
    );

    public async Task<List<Package>> GetPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        await FetchPackageIndexAutomaticAsync(progressFactory);
        return Packages;
    }

    internal async Task<List<Package>?> ParsePackageIndexAsync(string packageIndexBasePath)
    {
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
        return allPackages;
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

    protected void ProcessPackages(List<Package> packages)
    {
        foreach (var package in packages)
        {
            ProcessPackage(package);
        }
    }

    protected void ProcessPackage(Package package)
    {
        package.Source = this;
        nameToPackage[package.FullName] = package;
    }

    public override string ToString()
    {
        var url = Service.Uri;
        return url.ToString();
    }

    public bool IsPackageDownloaded(VisualPackageVersion packageVersion) =>
        IsPackageDownloaded(packageVersion, out _, out _, out _);

    public abstract bool IsPackageDownloaded(
        VisualPackageVersion packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    );

    public abstract Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    );

    public virtual async Task<string?> ExtractAsync(
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
