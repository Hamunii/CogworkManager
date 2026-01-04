using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public class PackageRepo : ISaveWithJson<PackageRepo.RepositoryCache>
{
    public class Repository
    {
        public enum Kind
        {
            Thunderstore,
            CogV1,
        }

        public static Repository Thunderstore { get; } =
            new(new("https://thunderstore.io"), Kind.Thunderstore);

        internal ConcurrentDictionary<string, Package> nameToPackage = [];
        internal ConcurrentDictionary<string, Repository> urlToRepository = [];

        public Repository(Uri url, Kind repoKind)
        {
            Url = url;
            RepoKind = repoKind;
            urlToRepository.TryAdd(Url.ToString(), this);
        }

        public Repository GetActualRepository() => urlToRepository[Url.ToString()];

        [JsonInclude]
        public Uri Url { get; private set; }

        [JsonInclude]
        public Kind RepoKind { get; private set; }

        public override string ToString()
        {
            return Url.Authority + Url.PathAndQuery;
        }
    }

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
        public required SteamId? Steam { get; init; }
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

    public static PackageRepo Silksong { get; } = new(Game.Silksong, Repository.Thunderstore);

    [JsonIgnore]
    public string CacheFileLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(_game.Slug).FullName,
            "cache.json"
        );

    [JsonIgnore]
    public string CacheRepoIndexLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(_game.Slug).FullName,
            $"repo-index.json"
        );

    internal RepositoryCache RepoCache
    {
        get
        {
            if (field is { })
                return field;

            if (File.Exists(CacheFileLocation))
            {
                using var stream = File.OpenRead(CacheFileLocation);
                try
                {
                    var cache = JsonSerializer.Deserialize<RepositoryCache>(stream);
                    if (cache is { })
                        return field = cache;
                }
                catch (JsonException ex)
                {
                    Console.Error.Write("Error reading cache file: ");
                    Console.Error.WriteLine(ex);
                }
            }

            return field = new RepositoryCache();
        }
    }

    public List<Package> Packages { get; init; } = [];
    readonly Game _game;
    public Repository Repo { get; }
    bool isImported;

    public PackageRepo(Game game, Repository repository)
    {
        _game = game;
        Repo = repository;
    }

    public void FetchIfShould()
    {
        Console.WriteLine("Previous fetch: " + RepoCache.LastRepositoryFetch);

        var dateNow = DateTime.Now;
        var lastFetch = RepoCache.LastRepositoryFetch;

        bool fetchAgain = false;

        if (dateNow < lastFetch)
            fetchAgain = true;

        if (dateNow > lastFetch.AddMinutes(1))
            fetchAgain = true;

        if (!fetchAgain)
        {
            if (!isImported)
            {
                Import();
            }
            return;
        }

        RepoCache.LastRepositoryFetch = dateNow;
        Console.WriteLine("New fetch: " + RepoCache.LastRepositoryFetch);
        this.Save(RepoCache, CacheFileLocation);
        // TODO: Implement for real
        Import();
    }

    public List<Package> GetAllPackages()
    {
        FetchIfShould();
        return Packages;
    }

    public ModList GetModList(string name, ModList.ModListConfig? config = null)
    {
        config ??= new() { Repositories = [Repository.Thunderstore] };
        return new ModList(_game, name, config);
    }

    internal void Import()
    {
        if (File.Exists(CacheRepoIndexLocation))
        {
            using var stream = File.OpenRead(CacheRepoIndexLocation);
            try
            {
                var packages = JsonSerializer.Deserialize<List<Package>>(stream);
                if (packages is { })
                {
                    foreach (var package in packages)
                    {
                        _ = Repo
                            .nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>()
                            .TryAdd(package.FullName, package);
                    }
                    foreach (var package in packages)
                    {
                        Console.WriteLine(package);
                    }
                    isImported = true;
                    return;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.Write("Error reading cache file: ");
                Console.Error.WriteLine(ex);
            }
        }

        throw new NotImplementedException(
            $"Repository file '{CacheRepoIndexLocation}' must exist for now."
        );
    }
}
