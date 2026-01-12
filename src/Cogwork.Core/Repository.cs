global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Core;

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
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
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
    public string PackageIndexLocation { get; }
    public string PackageIndexCacheLocation { get; }
    public Uri Url { get; }
    public Game Game { get; }
}

public sealed class ThunderstoreCommunity(Game game) : IPackageSourceService
{
    [JsonIgnore]
    public string PackageIndexLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(game.Slug),
            $"thunderstore-index.json"
        );
    public Uri Url { get; } = new($"https://thunderstore.io/c/{game.Slug}/");
    public Game Game => game;

    public string PackageIndexCacheLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(game.Slug),
            $"thunderstore-index-cache.json"
        );

    public async Task<bool> FetchIndexFileToCacheAsync()
    {
        var url = $"https://thunderstore.io/c/{game.Slug}/api/v1/package-listing-index/";

        Cog.Information("Fetching: " + url);

        using HttpClient client = new();
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Cog.Error("Error fetching url for package index: " + response.StatusCode);
            return false;
        }

        string packageIndexUrl;
        {
            using GZipStream zipStream = new(
                response.Content.ReadAsStream(),
                CompressionMode.Decompress
            );
            var strings = JsonSerializer.Deserialize<string[]>(zipStream);
            if (strings is not { Length: 1 })
            {
                if (strings is null)
                {
                    Cog.Error($"Expected string[] but received null from '{url}'.");
                    return false;
                }

                Cog.Error($"Expected 1 string but received {strings.Length} from '{url}'.");
                return false;
            }
            packageIndexUrl = strings[0];
            Cog.Debug("Got package index url: " + packageIndexUrl);
        }

        response = await client.GetAsync(packageIndexUrl);
        if (!response.IsSuccessStatusCode)
        {
            Cog.Error("Error fetching package index url: " + response.StatusCode);
            return false;
        }

        {
            using GZipStream zipStream = new(
                response.Content.ReadAsStream(),
                CompressionMode.Decompress
            );
            using var fileStream = File.OpenWrite(PackageIndexLocation);
            zipStream.CopyTo(fileStream);
        }

        Cog.Information("Fetched successfully.");

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
    public PackageSource Default => PackageSources[0];

    [JsonIgnore]
    public ReadOnlyCollection<PackageSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<PackageSource> PackageSources { get; } = [];

    public PackageSourceIndex(PackageSource packageSource)
    {
        Add(packageSource);
    }

    public void Add(PackageSource packageSource)
    {
        PackageSources.Add(packageSource);
        packageSource.SourceIndex = this;
    }

    public async Task<IEnumerable<Package>> GetAllPackagesAsync()
    {
        var fetchTasks = PackageSources.Select(x => x.GetPackagesAsync());
        Task.WaitAll(fetchTasks);
        return fetchTasks.SelectMany(x => x.Result);
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
                field ??= GlobalConfig.LoadSavedData(GlobalConfigLocation);

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
                    Instance.Save(GlobalConfigLocation);
                }
            }
        }

        public sealed class GameConfig : ISaveWithJson
        {
            [JsonIgnore]
            public Game? Game { get; set; }

            [JsonIgnore]
            public ModList? ActiveProfile { get; set; }
            public string? ActiveProfileName
            {
                get => ActiveProfile?.Name ?? field;
                set
                {
                    if (Game is null || value is null)
                    {
                        field = value;
                        return;
                    }

                    if (Game.NameToProfile.TryGetValue(value, out var profile))
                    {
                        ActiveProfile = profile;
                    }
                }
            }

            public void ConnectGame(Game game)
            {
                var previousProfileName = ActiveProfileName;
                Game = game;
                ActiveProfileName = previousProfileName;
            }
        }

        public GameConfig Config
        {
            get
            {
                if (field is { })
                    return field;

                field = GameConfig.LoadSavedData(GameConfigLocation);
                field.ConnectGame(this);
                return field;
            }
        }

        [JsonIgnore]
        public string GameConfigLocation =>
            field ??= Path.Combine(CogworkPaths.GetDataSubDirectory(Slug), $"config.json");

        public ConcurrentDictionary<string, ModList> NameToProfile { get; } = [];

        public static Game Silksong { get; } =
            new()
            {
                Name = "Hollow Knight: Silksong",
                Slug = "hollow-knight-silksong",
                Platforms = new() { Steam = new() { Id = 1030300 } },
            };

        public static Game Milksong { get; } =
            new()
            {
                Name = "Hollow Knight: Milksong",
                Slug = "hollow-knight-Milksong",
                Platforms = new() { Steam = new() { Id = 1030300 } },
            };

        public static Game HollowKnight { get; } =
            new()
            {
                Name = "Hollow Knight",
                Slug = "hollow-knight",
                Platforms = new(),
            };

        public static Game LethalCompany { get; } =
            new()
            {
                Name = "Lethal Company",
                Slug = "lethal-company",
                Platforms = new(),
            };

        public static Game Ror2 { get; } =
            new()
            {
                Name = "Risk of Rain 2",
                Slug = "risk-of-rain-2",
                Platforms = new(),
            };

        public static IEnumerable<Game> SupportedGames { get; } =
        [Silksong, HollowKnight, Milksong, LethalCompany, Ror2];

        public static ConcurrentDictionary<string, Game> NameToGame { get; } =
            new([
                .. SupportedGames.Select(x => KeyValuePair.Create(x.Name.ToLowerInvariant(), x)),
                .. SupportedGames.Select(x => KeyValuePair.Create(x.Slug, x)),
            ]);

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("slug")]
        public required string Slug { get; init; }

        [JsonPropertyName("platforms")]
        public required Platforms Platforms { get; init; }
    }

    public static PackageSource ThunderstoreSilksong { get; } =
        new(new ThunderstoreCommunity(Game.Silksong));

    public static PackageSourceIndex Silksong { get; } = new(ThunderstoreSilksong);

    public static double SecondsUntilAutomaticIndexRefreshAllowed { get; } = 60d * 20d;
    public static double SecondsUntilManualIndexRefreshAllowed { get; } = 10d;

    internal PackageSourceCache SourceCache =>
        field ??= PackageSourceCache.LoadSavedData(Service.PackageIndexCacheLocation);

    public PackageSourceIndex SourceIndex { get; internal set; } = null!;
    public IPackageSourceService Service { get; private set; }
    private List<Package> Packages { get; set; } = [];

    internal ConcurrentDictionary<string, Package> nameToPackage = [];
    bool isImported;

    public PackageSource(IPackageSourceService service)
    {
        Service = service;
    }

    public async Task FetchPackageIndexAutomaticAsync()
    {
        _ = await FetchPackageIndexAsync(SecondsUntilAutomaticIndexRefreshAllowed);
    }

    public async Task FetchPackageIndexManualAsync()
    {
        _ = await FetchPackageIndexAsync(SecondsUntilManualIndexRefreshAllowed);
    }

    private async Task<bool> FetchPackageIndexAsync(double secondsUntilIndexRefreshAllowed)
    {
        var dateNow = DateTime.Now;
        var lastFetch = SourceCache.LastFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.AddSeconds(secondsUntilIndexRefreshAllowed))
            fetchAgain = true;

        if (fetchAgain)
        {
            switch (Service)
            {
                case ThunderstoreCommunity ts:
                    _ = await ts.FetchIndexFileToCacheAsync();
                    break;
                default:
                    Cog.Error("Invalid RepoHandler type: " + Service.GetType());
                    return false;
            }

            SourceCache.LastFetch = dateNow;
            SourceCache.Save(Service.PackageIndexCacheLocation);
        }
        else
        {
            Cog.Information(
                $"Using cached package index for '{Service.Url}', last fetch was "
                    + $"less than {secondsUntilIndexRefreshAllowed} seconds ago."
            );

            if (isImported)
            {
                return true;
            }
        }

        if (!TryParsePackageIndexFile(Service.PackageIndexLocation, out var packages))
        {
            Cog.Error("Package index parsing failed");
            return false;
        }
        Packages = packages;
        isImported = true;
        return true;
    }

    public async Task<List<Package>> GetPackagesAsync()
    {
        await FetchPackageIndexAutomaticAsync();
        return Packages;
    }

    public ModList GetModList(string name)
    {
        return new ModList(Service.Game, name, SourceIndex);
    }

    internal bool TryParsePackageIndexFile(
        string packageIndexPath,
        [NotNullWhen(true)] out List<Package>? packages
    )
    {
        if (!File.Exists(packageIndexPath))
        {
            Cog.Error($"Package index file '{packageIndexPath}' must exist.");
            packages = default;
            return false;
        }

        var data = File.ReadAllText(packageIndexPath);
        return TryParsePackageIndex(data, out packages);
    }

    internal bool TryParsePackageIndex(string data, [NotNullWhen(true)] out List<Package>? packages)
    {
        try
        {
            packages = JsonSerializer.Deserialize<List<Package>>(
                data,
                ISaveWithJsonExtensions.Options
            );
            if (packages is null)
            {
                Cog.Error("Package index file json deserialization returned null");
                return false;
            }
            for (int i = 0; i < packages.Count; i++)
            {
                Package package = packages[i];
                package.PackageRepo = this;

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
        var url = Service.Url;
        return url.Authority + url.PathAndQuery;
    }
}
