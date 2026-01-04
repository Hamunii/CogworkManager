using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public class PackageRepo : ISaveWithJson<PackageRepo.RepositoryCache>
{
    public class Repository(Uri url, Repository.Kind repoKind)
    {
        public enum Kind
        {
            Thunderstore,
            CogV1,
        }

        public Uri Url { get; } = url;
        public Kind RepoKind { get; } = repoKind;
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

    public static Repository Thunderstore { get; } =
        new(new("https://thunderstore.io"), Repository.Kind.Thunderstore);

    public static PackageRepo Silksong { get; } = new(Game.Silksong, Thunderstore);

    public string FileLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheSubDirectory(_game.Slug).FullName,
            "cache.json"
        );

    internal RepositoryCache RepoCache
    {
        get
        {
            if (field is { })
                return field;

            if (File.Exists(FileLocation))
            {
                using var stream = File.OpenRead(FileLocation);
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

    static readonly Package[] packages = [];

    readonly Game _game;
    public Repository Repo { get; }

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
            return;

        RepoCache.LastRepositoryFetch = dateNow;
        Console.WriteLine("New fetch: " + RepoCache.LastRepositoryFetch);
        this.Save(RepoCache);
    }

    public Package[] GetAllPackages()
    {
        FetchIfShould();
        return packages;
    }

    public ModList GetModList(string name)
    {
        return new ModList(_game, name);
    }
}
