using System.Data;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using Downloader;
using ZLinq;

namespace Cogwork.Core.Sources;

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

    protected static string GetPackageVersionUrlPath(PackageVersion package) =>
        $"{package.Author}/{package.Name}/{package.Version}";

    protected virtual string GetPackageListingIndexUrl() =>
        $"https://{id.Site}/c/{id.GameSlug}/api/v1/package-listing-index/";

    protected virtual string GetPackageDownloadUrl(PackageVersion package) =>
        $"https://{id.Site}/package/download/{GetPackageVersionUrlPath(package)}/";

    protected virtual string GetPackageReadmeUrl(PackageVersion package) =>
        $"https://{id.Site}/api/experimental/package/{GetPackageVersionUrlPath(package)}/readme/";

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexIndexDirectory)
            .Any(x => x.EndsWith(".next", StringComparison.Ordinal));

    public string PackageIndexLocation(string hash) =>
        Path.Combine(PackageIndexIndexDirectory, hash);

    public override async Task<bool> FetchPackageIndexAsync(
        bool maybeRefetch,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (!maybeRefetch && isImported)
            return true;

        var dateNow = DateTime.Now;
        var lastFetch = SourceCache.LastFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.Add(TimeSpan.FromSeconds(10)))
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
            Cog.Information($"Using cached package index for '{Service.Uri}'");

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
        PackageVersionReference packageVersion,
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
        var PackageVersionReference = (PackageVersionReference)packageVersion;
        if (IsPackageDownloaded(PackageVersionReference, out string? zipFileLocation, out _, out _))
        {
            Cog.Debug($"Package is already downloaded for '{packageVersion}'");
            return true;
        }

        var downloadUrl = GetPackageDownloadUrl(packageVersion);
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
        PackageVersionReference packageVersion,
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

        var downloadUrl = GetPackageReadmeUrl(packageVersion);

        var downloader = new DownloadService(Utils.SharedDownloadConfiguration);

        var failed = false;
        downloader.DownloadFileCompleted += (_, e) =>
        {
            if (!e.Cancelled && e.Error is null)
                return;

            failed = true;
            if (e.Cancelled)
                Cog.Warning($"Cancelled downloading package readme '{packageVersion}'");
            else if (e.Error is { } ex)
                Cog.Error($"Error downloading package readme '{packageVersion}': {ex}");
        };

        using var memoryStream = await downloader.DownloadFileTaskAsync(
            downloadUrl,
            cancellationToken
        );

        if (failed)
        {
            return "failed to fetch readme";
        }

        var markdown = JsonSerializer.Deserialize(memoryStream, JsonGen.Default.PackageMarkdown);

        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(readmeTodo, markdown.Markdown, cancellationToken);

        File.Move(readmeTodo, readme);
        return markdown.Markdown;
    }
}
