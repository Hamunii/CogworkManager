using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using Downloader;
using ZLinq;

namespace Cogwork.Core;

public sealed class LocalPackageSource : PackageSource
{
    public static LocalPackageSource Instance { get; } = new();

    public override PackageSourceId Uri { get; } = new("local", string.Empty);

    public override string Id => field ??= Uri.ToString();

    public string PackageIndexPath { get; } =
        Path.Combine(CogworkPaths.GetPackagesSubDirectory("local"), "local-index.json");

    DateTime _lastFetch;

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
            )
            ||
            // I don't feel good about this because we are passing the "latest" package's
            // paths, but the API promises that one of those paths is true if this returns true,
            // but if we return true here and the previous didn't, this implementation doesn't
            // conform to the API.
            IsPackageDownloaded(packageVersion, withVersionName: "next", out _, out _, out _);
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
        var visualPackageVersion = (VisualPackageVersion)packageVersion;
        if (!IsPackageDownloaded(visualPackageVersion))
        {
            Cog.Error(
                $"Attempting to download local package '{packageVersion}' which is not found."
            );
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public override async Task<string> GetReadmeAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        var dir =
            await ExtractAsync(packageVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Local package should always be installed");

        return await File.ReadAllTextAsync(Path.Combine(dir, "README.md"), cancellationToken);
    }

    internal static bool NeedsReinstall(VisualPackageVersion packageVersion) =>
        IsPackageDownloaded(
            packageVersion,
            withVersionName: "next",
            out var _,
            out var _,
            out var _
        );

    public override async Task<string?> ExtractAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    )
    {
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
            _ = IsPackageDownloaded(
                packageVersion,
                withVersionName: "latest",
                out var zipPathFinal,
                out var directoryPathFinal,
                out var zipExistsFinal
            );

            if (Directory.Exists(directoryPathFinal))
            {
                Directory.Delete(directoryPathFinal, recursive: true);
            }

            if (Directory.Exists(directoryPathTemp))
            {
                Directory.Move(directoryPathTemp, directoryPathFinal);
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

    public override async Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (_lastFetch > DateTime.Now - TimeSpan.FromSeconds(2))
        {
            Cog.Debug("Local package index is already fetched");
            return true;
        }

        if (!File.Exists(PackageIndexPath))
        {
            Cog.Debug("Fetched local package index (which has not been created yet)");
            return true;
        }

        using var fileStream = File.Open(
            PackageIndexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        var packages = JsonSerializer.Deserialize(fileStream, JsonGen.Default.ListPackage);
        if (packages is null)
        {
            Packages ??= [];
            Cog.Error($"Package index file '{PackageIndexPath}' deserialization returned null");
            return false;
        }
        ProcessPackages(packages);
        Packages = packages;

        _lastFetch = DateTime.Now;
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

        packageVersion = packageVersion.WithVersion(
            packageVersion.Version.WithMetadata($"id.{Guid.NewGuid():N}")
        );

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

        Cog.Information($"Imported package '{packageVersion}'");

        _lastFetch = DateTime.Now;
        return null;
    }
}

public class ThunderstoreCommunity(PackageSourceId id) : PackageSource
{
    [JsonIgnore]
    public string PackageIndexBaseDirectory =>
        field ??= CogworkPaths.GetCacheIndexSubDirectory(id.GameSlug, id.Site);

    public string PackageIndexIndexDirectory =>
        field ??= CogworkPaths.CombineAndCreate(PackageIndexBaseDirectory, "index");

    public string PackageInstallSubDirectory { get; } = id.Site;

    public override PackageSourceId Uri => id;
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

    public static ThunderstoreCommunity CreateDefault(Game game) =>
        new(new("thunderstore.io", game.Slug));

    protected static string GetPackageVersionUrlPath(VisualPackageVersion package) =>
        $"{package.Author}/{package.Name}/{package.Version}";

    protected virtual string GetPackageListingIndexUrl() =>
        $"https://{id.Site}/c/{id.GameSlug}/api/v1/package-listing-index/";

    protected virtual string GetPackageDownloadUrl(VisualPackageVersion package) =>
        $"https://{id.Site}/package/download/{GetPackageVersionUrlPath(package)}/";

    protected virtual string GetPackageReadmeUrl(VisualPackageVersion package) =>
        $"https://{id.Site}/api/experimental/package/{GetPackageVersionUrlPath(package)}/readme/";

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexIndexDirectory)
            .Any(x => x.EndsWith(".next", StringComparison.Ordinal));

    public string PackageIndexLocation(string hash) =>
        Path.Combine(PackageIndexIndexDirectory, hash);

    public override async Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
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

            var fetchResult = await FetchIndexToCacheAsync(progress, cancellationToken);
            if (!fetchResult.Performed)
                return true;

            if (!fetchResult.Value)
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

    public async Task<PerformedOrNot<bool>> FetchIndexToCacheAsync(
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        var result = await Utils.DoTaskOrWaitForCompletionAsync(
            PackageIndexBaseDirectory,
            progress,
            DoIndexFetchLogicAsync,
            cancellationToken
        );

        return result;
    }

    async Task<bool> DoIndexFetchLogicAsync(
        ProgressContext progress,
        CancellationToken cancellationToken = default
    )
    {
        var url = GetPackageListingIndexUrl();

        Cog.Information("Fetching: " + url);

        HttpClient client = Utils.SharedHttpClient;
        var config = Utils.SharedDownloadConfiguration;

        if (await FetchPackageListingsAsync(url, client, cancellationToken) is not { } listings)
        {
            return false;
        }

        if (
            !await DownloadIndexesAsync(
                progress,
                config,
                listings.allPackageIndexUrls,
                listings.newPackageIndexUrls,
                cancellationToken
            )
        )
        {
            return false;
        }

        Cog.Information("Fetched successfully.");
        return true;
    }

    private async Task<(
        (string url, string fileName)[] allPackageIndexUrls,
        (string url, string fileName)[] newPackageIndexUrls
    )?> FetchPackageListingsAsync(
        string url,
        HttpClient client,
        CancellationToken cancellationToken
    )
    {
        var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Cog.Error("Error fetching url for package index: " + response.StatusCode);
            return default;
        }

        using GZipStream zipStream = new(
            response.Content.ReadAsStream(cancellationToken),
            CompressionMode.Decompress
        );
        var strings = JsonSerializer.Deserialize(zipStream, JsonGen.Default.StringArray);
        if (strings is null)
        {
            Cog.Error($"Expected string[] but received null from '{url}'.");
            return default;
        }

        (string url, string fileName)[] allPackageIndexUrls;
        (string url, string fileName)[] newPackageIndexUrls;

        allPackageIndexUrls = [.. strings.Select(url => (url, url.Split('/')[^1]))];
        newPackageIndexUrls =
        [
            .. allPackageIndexUrls.Where(x =>
            {
                var filePath = PackageIndexLocation(x.fileName);
                return !(File.Exists(filePath) || File.Exists(filePath + ".next"));
            }),
        ];

        Cog.Debug(
            $"Got package index urls: {newPackageIndexUrls.Length} new, {strings.Length} total"
        );
        return (allPackageIndexUrls, newPackageIndexUrls);
    }

    private async Task<bool> DownloadIndexesAsync(
        ProgressContext progress,
        DownloadConfiguration downloadConfig,
        (string url, string fileName)[] allPackageIndexUrls,
        (string url, string fileName)[] newPackageIndexUrls,
        CancellationToken cancellationToken = default
    )
    {
        Cog.Debug(
            $"Downloading {newPackageIndexUrls.Length} package indexes to {PackageIndexIndexDirectory}"
        );

        long combinedTotalBytes = 0;
        long totalContentLength = 0;
        bool anyDownloadFailed = false;

        if (progress.Progress is { } combinedProgress && progress.OnContentLengthKnown is { })
        {
            Cog.Debug("Fetching combined downloadable index size");

            await Parallel.ForEachAsync(
                newPackageIndexUrls,
                new ParallelOptions()
                {
                    MaxDegreeOfParallelism = 32, // enough for all package listings at once
                    CancellationToken = cancellationToken,
                },
                async (fileInfo, cancellationToken) =>
                {
                    var info = await RemoteFileResolver.GetFileInfoAsync(
                        fileInfo.url,
                        Utils.SharedDownloadConfiguration,
                        cancellationToken
                    );

                    if (info.FileSize != -1)
                    {
                        Interlocked.Add(ref totalContentLength, info.FileSize);
                    }
                }
            );

            progress.OnContentLengthKnown(combinedProgress, totalContentLength);
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 6, // arbitrary value
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            newPackageIndexUrls,
            parallelOptions,
            async (fileInfo, cancellationToken) =>
            {
                var downloader = new DownloadService(downloadConfig);

                long previousTotalBytes = 0;

                if (progress.Progress is { } combinedProgress)
                {
                    downloader.DownloadProgressChanged += (_, e) =>
                    {
                        long totalBytes = e.ReceivedBytesSize;
                        long diff = totalBytes - previousTotalBytes;
                        previousTotalBytes = totalBytes;
                        Interlocked.Add(ref combinedTotalBytes, diff);

                        lock (_totalBytesLock)
                        {
                            combinedProgress.Report(combinedTotalBytes);
                        }
                    };
                }

                var packageIndexLocation = PackageIndexLocation(fileInfo.fileName);
                var tempDestinationPath = packageIndexLocation + ".next";

                downloader.DownloadFileCompleted += (_, e) =>
                {
                    if (!e.Cancelled && e.Error is null)
                    {
                        Cog.Debug($"Downloaded index {fileInfo.fileName}");
                        return;
                    }

                    anyDownloadFailed = true;
                    if (e.Cancelled)
                        Cog.Warning($"Cancelled fetching package index url '{fileInfo.url}'");
                    else if (e.Error is { } ex)
                        Cog.Error($"Error fetching package index url '{fileInfo.url}': {ex}");
                };

                await downloader.DownloadFileTaskAsync(
                    fileInfo.url,
                    tempDestinationPath,
                    cancellationToken
                );
            }
        );

        if (anyDownloadFailed)
        {
            return false;
        }

        foreach (var url in allPackageIndexUrls)
        {
            var packageIndexLocation = PackageIndexLocation(url.fileName);
            var nextFile = packageIndexLocation + ".next";
            if (File.Exists(nextFile))
            {
                File.Move(nextFile, packageIndexLocation, overwrite: true);
            }
        }

        var allUpToDateFiles = allPackageIndexUrls.Select(x => x.fileName).ToHashSet();
        foreach (var outdated in Directory.EnumerateFiles(PackageIndexIndexDirectory))
        {
            if (!allUpToDateFiles.Contains(Path.GetFileName(outdated)))
            {
                Cog.Debug($"Deleting outdated cache file '{outdated}'");
                File.Delete(outdated);
            }
        }

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
        var visualPackageVersion = (VisualPackageVersion)packageVersion;
        if (IsPackageDownloaded(visualPackageVersion, out string? zipFileLocation, out _, out _))
        {
            Cog.Debug($"Package is already downloaded for '{packageVersion}'");
            return true;
        }

        var downloadUrl = GetPackageDownloadUrl(visualPackageVersion);
        Cog.Debug($"Attempting to download: {downloadUrl}");

        var downloader = new DownloadService(Utils.SharedDownloadConfiguration);
        downloader.TrackDownloadProgress(progress);

        var success = true;
        downloader.DownloadFileCompleted += (_, e) =>
        {
            if (!e.Cancelled && e.Error is null)
            {
                Cog.Debug($"Download complete for: {downloadUrl}");
                return;
            }

            success = false;
            if (e.Cancelled)
                Cog.Warning($"Cancelled downloading package '{packageVersion}'");
            else if (e.Error is { } ex)
                Cog.Error($"Error downloading package '{packageVersion}': {ex}");
        };

        await downloader.DownloadFileTaskAsync(downloadUrl, zipFileLocation, cancellationToken);
        return success;
    }

    public override async Task<string> GetReadmeAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        var version = packageVersion.Version.ToString();
        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            PackageInstallSubDirectory,
            packageVersion.FullName
        );

        var directoryPath = Path.Combine(installPathRoot, version);
        var readme = Path.Combine(directoryPath, "README.md");
        var readmeTodo = readme + ".next";

        if (File.Exists(readme))
        {
            return await File.ReadAllTextAsync(readme, cancellationToken);
        }

        var author = packageVersion.Author;
        var name = packageVersion.Name;
        var downloadUrl = GetPackageReadmeUrl(packageVersion);

        using MemoryStream memoryStream = new();

        HttpClient client = Utils.SharedHttpClient;
        var statusCode = await client.DownloadAsync(
            downloadUrl,
            memoryStream,
            default,
            cancellationToken
        );

        if (!statusCode.IsSuccess)
        {
            Cog.Error($"Error downloading package readme '{packageVersion}': " + statusCode);
            return "failed to fetch readme";
        }
        memoryStream.Position = 0;
        var markdown = JsonSerializer.Deserialize(memoryStream, JsonGen.Default.PackageMarkdown);

        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(readmeTodo, markdown.Markdown, cancellationToken);

        File.Move(readmeTodo, readme);
        return markdown.Markdown;
    }
}

[JsonConverter(typeof(PackageSourceIdConverter))]
public readonly record struct PackageSourceId(string Site, string GameSlug)
{
    public static PackageSourceId Parse(string sourceId)
    {
        var split = sourceId.Split('/');
        if (split.Length > 2)
            throw new ArgumentException(
                $"Argument '{sourceId}' must have at max one divider ('/')"
            );

        var site = split[0];
        var game = split.Length < 2 ? string.Empty : split[1];

        return new(site, game);
    }

    public bool TryGetGame([NotNullWhen(true)] out Game? game) =>
        Game.NameToGame.TryGetValue(GameSlug, out game);

    public override string ToString() => GameSlug == string.Empty ? Site : $"{Site}/{GameSlug}";
}

public readonly record struct PackageMarkdown(
    [property: JsonPropertyName("markdown")] string Markdown
);

public abstract class PackageSource
{
    public sealed class PackageSourceCache : ISaveWithJson
    {
        public DateTime LastFetch { get; set; }
    }

    public PackageSource Service => this;
    protected List<Package> Packages { get; set; } = [];
    public abstract PackageSourceId Uri { get; }
    public abstract string Id { get; }

    internal ConcurrentDictionary<string, Package> nameToPackage = [];

    public async Task FetchPackageIndexAutomaticAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        _ = await FetchPackageIndexAsync(
            TimeSpan.FromMinutes(20),
            progressFactory,
            cancellationToken
        );
    }

    public async Task FetchPackageIndexManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        _ = await FetchPackageIndexAsync(
            TimeSpan.FromSeconds(10),
            progressFactory,
            cancellationToken
        );
    }

    public abstract Task<bool> FetchPackageIndexAsync(
        TimeSpan timeUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
    );

    public async Task<List<Package>> GetPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        await FetchPackageIndexAutomaticAsync(progressFactory, cancellationToken);
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
            Directory
                .EnumerateFiles(packageIndexPath)
                .Where(x =>
                    !x.EndsWith(".next", StringComparison.Ordinal)
                    && !x.EndsWith(".next.download", StringComparison.Ordinal)
                ),
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

    public bool TryImportHiddenUniquePackage(
        PackageVersion packageVersion,
        [NotNullWhen(true)] out Package? package
    )
    {
        if (packageVersion.Package is not null)
        {
            throw new ArgumentException(
                $"PackageVersion '{packageVersion}' must not belong to a Package."
            );
        }

        if (nameToPackage.ContainsKey(packageVersion.GetFullName()))
        {
            package = null;
            return false;
        }

        package = new Package(packageVersion.Author, packageVersion.Name, [packageVersion]);
        ProcessPackage(package);
        return true;
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

    public abstract Task<string> GetReadmeAsync(
        VisualPackageVersion packageVersion,
        CancellationToken cancellationToken = default
    );
}
