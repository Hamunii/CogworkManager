global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.GamePackageRepo;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Core;

namespace Cogwork.Core;

internal static class CogworkCoreLogger
{
    static CogworkCoreLogger()
    {
        var assembly = typeof(CogworkCoreLogger).Assembly;
        var name = assembly.GetName().Name;
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Cog.Debug($"{name} {version} initialized.");
    }

    static string LogFileLocation =>
        Path.Combine(CogworkPaths.GetCacheSubDirectory("logs").FullName, "log-.txt");

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

public interface IGamePackageRepoHandler
{
    public string RepoIndexLocation { get; }
    public string RepoIndexCacheLocation { get; }
    public Uri Url { get; }
    public Game Game { get; }
    public string Type { get; }
}

public class RepoThunderstoreHandler(Game game) : IGamePackageRepoHandler
{
    [JsonIgnore]
    public string RepoIndexLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(game.Slug).FullName,
            $"thunderstore-index.json"
        );
    public Uri Url { get; } = new($"https://thunderstore.io/c/{game.Slug}/");
    public Game Game => game;
    public string Type => "Thunderstore";

    public string RepoIndexCacheLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(game.Slug).FullName,
            $"thunderstore-index-cache.json"
        );

    public async Task<bool> FetchIndexFileToCache()
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
            using var fileStream = File.OpenWrite(RepoIndexLocation);
            zipStream.CopyTo(fileStream);
        }

        Cog.Information("Fetched successfully.");

        return true;
    }
}

public class GamePackageRepoList
{
    /// <summary>
    /// The package repo which is resolved when a package source is not defined.
    /// This should be Thunderstore, if Thunderstore is present.
    /// </summary>
    [JsonIgnore]
    public GamePackageRepo Default => Repos[0];

    [JsonIgnore]
    public ReadOnlyCollection<GamePackageRepo> Repos => field ??= new(PackageRepos);

    [JsonIgnore]
    public IEnumerable<Package> AllPackages => PackageRepos.SelectMany(x => x.Packages);

    [JsonIgnore]
    List<GamePackageRepo> PackageRepos { get; } = [];

    public GamePackageRepoList(List<GamePackageRepo> packageRepos)
    {
        PackageRepos = packageRepos;
        foreach (var repo in packageRepos)
        {
            repo.RepoList = this;
        }
    }

    public void Add(GamePackageRepo packageRepo)
    {
        PackageRepos.Add(packageRepo);
        packageRepo.RepoList = this;
    }
}

public class GamePackageRepo : ISaveWithJson<RepositoryCache>
{
    public class RepositoryCache
    {
        public DateTime LastRepositoryFetch { get; set; }
    }

    public readonly struct SteamId
    {
        [JsonPropertyName("id")]
        public required long Id { get; init; }
    }

    public class Platforms
    {
        [JsonPropertyName("steam")]
        public SteamId? Steam { get; init; }
    }

    public class Game
    {
        public static Game Silksong { get; } =
            new()
            {
                Name = "Hollow Knight: Silksong",
                Slug = "hollow-knight-silksong",
                Platforms = new() { Steam = new() { Id = 1030300 } },
            };

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("slug")]
        public required string Slug { get; init; }

        [JsonPropertyName("platforms")]
        public required Platforms Platforms { get; init; }
    }

    public static GamePackageRepo ThunderstoreSilksong { get; } =
        new(new RepoThunderstoreHandler(Game.Silksong));

    public static GamePackageRepoList Silksong { get; } = new([ThunderstoreSilksong]);
    public static double MinutesUntilAutomaticIndexRefreshAllowed { get; } = 20d;

    // TODO: Implement manual refresh.
    public static double SecondsUntilManualIndexRefreshAllowed { get; } = 10d;

    internal RepositoryCache RepoCache
    {
        get
        {
            if (field is { })
                return field;

            if (File.Exists(RepoHander.RepoIndexCacheLocation))
            {
                using var stream = File.OpenRead(RepoHander.RepoIndexCacheLocation);
                try
                {
                    var cache = JsonSerializer.Deserialize<RepositoryCache>(stream);
                    if (cache is { })
                        return field = cache;
                }
                catch (JsonException ex)
                {
                    Cog.Error("Error reading cache file: " + ex.ToString());
                }
            }

            return field = new RepositoryCache();
        }
    }

    public GamePackageRepoList RepoList { get; internal set; } = null!;
    public List<Package> Packages { get; private set; } = [];
    public IGamePackageRepoHandler RepoHander { get; private set; }

    internal ConcurrentDictionary<string, Package> nameToPackage = [];
    bool isImported;

    public GamePackageRepo(IGamePackageRepoHandler handler)
    {
        RepoHander = handler;
    }

    public async Task FetchPackageIndexAutomatic()
    {
        Cog.Debug("Previous fetch: " + RepoCache.LastRepositoryFetch);

        var dateNow = DateTime.Now;
        var lastFetch = RepoCache.LastRepositoryFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.AddMinutes(MinutesUntilAutomaticIndexRefreshAllowed))
            fetchAgain = true;

        if (!fetchAgain)
        {
            Cog.Information(
                $"Using cached package index for '{RepoHander.Url}', last fetch was "
                    + $"less than {MinutesUntilAutomaticIndexRefreshAllowed} minutes ago."
            );

            if (!isImported)
            {
                Import();
            }
            return;
        }

        switch (RepoHander)
        {
            case RepoThunderstoreHandler ts:
                _ = await ts.FetchIndexFileToCache();
                break;
            default:
                Cog.Error("Invalid RepoHandler type: " + RepoHander.GetType());
                return;
        }

        RepoCache.LastRepositoryFetch = dateNow;
        Cog.Debug("New fetch: " + RepoCache.LastRepositoryFetch);
        this.Save(RepoCache, RepoHander.RepoIndexCacheLocation);

        Import();
    }

    public async Task<List<Package>> GetAllPackages()
    {
        await FetchPackageIndexAutomatic();
        return Packages;
    }

    public ModList GetModList(string name, ModList.ModListConfig? config = null)
    {
        config ??= new() { RepoList = RepoList };
        return new ModList(RepoHander.Game, name, config);
    }

    internal void Import()
    {
        if (File.Exists(RepoHander.RepoIndexLocation))
        {
            var data = File.ReadAllText(RepoHander.RepoIndexLocation);
            if (Import(data))
            {
                isImported = true;
                return;
            }
        }

        throw new NotImplementedException(
            $"Package index file '{RepoHander.RepoIndexLocation}' must exist."
        );
    }

    internal bool Import(string data)
    {
        try
        {
            var packages = JsonSerializer.Deserialize<List<Package>>(
                data,
                ISaveWithJsonExtensions.Options
            );
            if (packages is { })
            {
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
                                RepoList,
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
                Packages = packages;
                return true;
            }
        }
        catch (JsonException ex)
        {
            Cog.Error("Error reading cache file: " + ex.ToString());
        }
        return false;
    }

    public override string ToString()
    {
        var url = RepoHander.Url;
        return url.Authority + url.PathAndQuery;
    }
}
