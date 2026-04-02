global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using Serilog;
using Serilog.Core;
using ZLinq;

namespace Cogwork.Core;

public static class CogworkCoreLogger
{
    static CogworkCoreLogger()
    {
        var assembly = typeof(CogworkCoreLogger).Assembly;
        var name = assembly.GetName().Name;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Cog.Debug($"=============================");
        Cog.Debug($"{name} {version} initialized.");
    }

    static string LogFileLocation =>
        Path.Combine(CogworkPaths.GetCacheSubDirectory("logs"), "log-.txt");

    public static Logger Cog { get; } =
        new LoggerConfiguration()
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
}

public interface IPackageSourceService
{
    public string PackageIndexDirectory { get; }
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

    public bool IsPackageDownloaded(PackageVersion packageVersion);

    public Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    );

    // Apparently one should preferably keep a singleton of HttpClient.
    internal static HttpClient SharedClient { get; } = new();
}

public sealed class ThunderstoreCommunity(Game game) : IPackageSourceService
{
    [JsonIgnore]
    public string PackageIndexDirectory =>
        field ??= CogworkPaths.GetCacheSubDirectory(game.Slug, "thunderstore");

    public string PackageInstallSubDirectory { get; } = "thunderstore";

    public Uri Uri { get; } = new($"https://thunderstore.io/c/{game.Slug}/");
    public string Id => field ??= Uri.ToString();
    public Game Game => game;

    public string PackageIndexCacheLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(game.Slug),
            $"thunderstore-index-cache.json"
        );

    readonly Lock _totalBytesLock = new();
    readonly Lock _totalContentLengthLock = new();

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexDirectory)
            .Any(x => x.EndsWith(".todo", StringComparison.Ordinal));

    public string PackageIndexLocation(string hash) => Path.Combine(PackageIndexDirectory, hash);

    public async Task<bool> FetchIndexToCacheAsync(ProgressContext progress = default)
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
            $"Downloading {newPackageIndexUrls.Length} package indexes to {PackageIndexDirectory}"
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
                    Cog.Debug($"Downloaded package {x.fileName}");
                    return status;
                }
            )
            .ToArray();

        Task.WaitAll(tasks);

        int j = 0;
        foreach (var status in tasks.Select((x) => x.Result))
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
                .EnumerateFiles(PackageIndexDirectory)
                .Where(x => !allUpToDateFiles.Contains(Path.GetFileName(x)))
        )
        {
            Cog.Debug($"Deleting outdated cache file '{outdated}'");
            File.Delete(outdated);
        }
        Cog.Information("Fetched successfully.");
        return true;
    }

    public bool IsPackageDownloaded(PackageVersion packageVersion) =>
        IsPackageDownloaded(packageVersion, out _);

    public bool IsPackageDownloaded(PackageVersion packageVersion, out string zipFileLocation)
    {
        var package = packageVersion.Package;
        var version = packageVersion.Version.ToString();
        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            PackageInstallSubDirectory,
            package.FullName
        );

        zipFileLocation = Path.Combine(installPathRoot, $"{version}.zip");
        return File.Exists(zipFileLocation);
    }

    public async Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        if (IsPackageDownloaded(packageVersion, out var zipFileLocation))
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
                Cog.Debug($"slug from uri: {slug} | {uri.AbsolutePath} | {uri}");

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
        Cog.Information($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.FetchPackageIndexAutomaticAsync(progressFactory))
            .ToArray();
#if DEBUG
        await Task.Delay(100);
#endif
        Task.WaitAll(fetchTasks);
    }
}

public sealed class PackageSource
{
    public sealed class PackageSourceCache : ISaveWithJson
    {
        public DateTime LastFetch { get; set; }
    }

    public readonly struct SteamId
    {
        [JsonPropertyName("id")]
        public required long Id { get; init; }
    }

    public sealed class Platforms
    {
        [JsonPropertyName("steam")]
        public SteamId? Steam { get; init; }
    }

    public sealed class Game
    {
        public sealed class GlobalConfig : ISaveWithJson
        {
            [JsonIgnore]
            public static string GlobalConfigLocation =>
                field ??= Path.Combine(CogworkPaths.DataDirectory, $"state.json");
            internal static GlobalConfig Instance =>
                field ??= GlobalConfig.LoadSavedDataOrNew(
                    GlobalConfigLocation,
                    JsonGen.Default.GlobalConfig
                );

            public string? ActiveGameSlug { get; set; }

            public static Game? ActiveGame
            {
                get
                {
                    if (field is { })
                        return field;

                    var config = Instance;
                    if (config.ActiveGameSlug is null)
                        return null;

                    if (!NameToGame.TryGetValue(config.ActiveGameSlug, out var game))
                    {
                        return null;
                    }

                    return field = game;
                }
                set
                {
                    field = value;
                    Instance.ActiveGameSlug = value?.Slug;
                    Instance.Save(GlobalConfigLocation, JsonGen.Default.GlobalConfig);
                }
            }
        }

        public sealed class GameConfig : ISaveWithJson
        {
            [JsonIgnore]
            public Game? Game { get; set; }

            [JsonIgnore]
            public LazyModList? ActiveProfile { get; set; }
            public string? ActiveProfileId
            {
                get => ActiveProfile?.Id ?? field;
                set
                {
                    if (Game is null || value is null)
                    {
                        field = value;
                        return;
                    }
                    if (ModList.GetFromId(Game, value) is { } modList)
                        ActiveProfile = modList;
                }
            }

            public void ConnectGame(Game game)
            {
                var activeProfileId = ActiveProfileId;
                Game = game;
                ActiveProfileId = activeProfileId;
            }
        }

        public GameConfig Config
        {
            get
            {
                if (field is { })
                    return field;

                field = GameConfig.LoadSavedDataOrNew(
                    GameConfigLocation,
                    JsonGen.Default.GameConfig
                );
                field.ConnectGame(this);
                return field;
            }
        }

        [JsonIgnore]
        public string GameConfigLocation =>
            field ??= Path.Combine(CogworkPaths.GetGamesSubDirectory(this), "config.json");

        public PackageSource? DefaultSource { get; }

        internal Game(string name, string slug, PackageSource? defaultSource = null)
        {
            Name = name;
            Slug = slug;
            DefaultSource = defaultSource ?? new(new ThunderstoreCommunity(this));
        }

        public static Game Silksong { get; } =
            new("Hollow Knight: Silksong", "hollow-knight-silksong")
            {
                Platforms = new() { Steam = new() { Id = 1030300 } },
            };

        public static Game LethalCompany { get; } =
            new("Lethal Company", "lethal-company") { Platforms = new() };

        public static Game Repo { get; } = new("R.E.P.O.", "repo") { Platforms = new() };
        public static Game Test { get; } =
            new("Test", "test", new(new TestPackageSource())) { Platforms = new() };
        public static Game Ror2 { get; } =
            new("Risk of Rain 2", "risk-of-rain-2") { Platforms = new() };

        public static List<Game> SupportedGames { get; } =
        [
            Silksong,
            LethalCompany,
            Repo,
            Ror2,
#if DEBUG
            Test,
#endif
        ];

        public static Dictionary<string, Game> NameToGame
        {
            get
            {
                Dictionary<string, Game> dict = new(SupportedGames.Count * 2);

                foreach (
                    var pair in SupportedGames
                        .AsValueEnumerable()
                        .Select(x => KeyValuePair.Create(x.Name.ToLowerInvariant(), x))
                        .Concat(SupportedGames.Select(x => KeyValuePair.Create(x.Slug, x)))
                )
                {
                    dict[pair.Key] = pair.Value;
                }

                return dict;
            }
        }

        [JsonPropertyName("name")]
        public string Name { get; init; }

        [JsonPropertyName("slug")]
        public string Slug { get; init; }

        [JsonPropertyName("platforms")]
        public required Platforms Platforms { get; init; }

        public IEnumerable<LazyModList> EnumerateProfiles()
        {
            DirectoryInfo profilesDir = new(CogworkPaths.GetProfilesDirectory(this));

            foreach (var profileDir in profilesDir.EnumerateDirectories())
            {
                var profile = ModList.GetFromId(this, profileDir.Name);
                if (profile is { } p)
                {
                    yield return p;
                }
            }
        }
    }

    public static PackageSource ThunderstoreSilksong { get; } =
        new(new ThunderstoreCommunity(Game.Silksong));

    public static PackageSourceIndex Silksong { get; } = new(ThunderstoreSilksong);

    public static double SecondsUntilAutomaticIndexRefreshAllowed { get; } = 60d * 5d;
    public static double SecondsUntilManualIndexRefreshAllowed { get; } = 10d;

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
        _ = await FetchPackageIndexAsync(SecondsUntilAutomaticIndexRefreshAllowed, progressFactory);
    }

    public async Task FetchPackageIndexManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _ = await FetchPackageIndexAsync(SecondsUntilManualIndexRefreshAllowed, progressFactory);
    }

    private async Task<bool> FetchPackageIndexAsync(
        double secondsUntilIndexRefreshAllowed,
        Func<PackageSource, ProgressContext>? progressFactory
    )
    {
        var dateNow = DateTime.Now;
        var lastFetch = SourceCache.LastFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.AddSeconds(secondsUntilIndexRefreshAllowed))
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
                    + $"less than {secondsUntilIndexRefreshAllowed} seconds ago."
            );

            if (isImported)
            {
                return true;
            }
        }

        if (!TryParsePackageIndexFile(Service.PackageIndexDirectory, out var packages))
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

    internal bool TryParsePackageIndexFile(
        string packageIndexPath,
        [NotNullWhen(true)] out List<Package>? packages
    )
    {
        if (!Directory.Exists(packageIndexPath))
        {
            Cog.Error($"Package index directory '{packageIndexPath}' must exist.");
            packages = default;
            return false;
        }

        List<Package> allPackages = [];

        var result = Parallel.ForEach(
            Directory.EnumerateFiles(packageIndexPath),
            (indexFile, state) =>
            {
                try
                {
                    using var fileStream = File.OpenRead(indexFile);
                    using GZipStream zipStream = new(fileStream, CompressionMode.Decompress);
                    if (!TryParsePackageIndex(zipStream, out var packages1))
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
            packages = default;
            return false;
        }

        packages = allPackages;
        return true;
    }

    internal bool TryParsePackageIndex(Stream data, [NotNullWhen(true)] out List<Package>? packages)
    {
        try
        {
            packages = JsonSerializer.Deserialize(data, JsonGen.Default.ListPackage);
            if (packages is null)
            {
                Cog.Error("Package index file json deserialization returned null");
                return false;
            }
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
            return true;
        }
        catch (JsonException ex)
        {
            Cog.Error("Error reading package index file: " + ex.ToString());
            packages = default;
            return false;
        }
    }

    public override string ToString()
    {
        var url = Service.Uri;
        return url.ToString();
    }
}
