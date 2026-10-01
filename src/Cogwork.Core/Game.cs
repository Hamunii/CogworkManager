using System.IO.Abstractions;
using System.Text.Json.Serialization;
using Cogwork.Core.Sources;
using ZLinq;

namespace Cogwork.Core;

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

public readonly record struct GlobalConfigData(string? ActiveGameSlug) : ISaveWithJson;

public sealed class GlobalConfig
{
    static string GlobalConfigLocation =>
        field ??= Path.Combine(CogworkPaths.DataDirectory, $"state.json");
    public static GlobalConfig Instance
    {
        get
        {
            if (field is { })
                return field;

            var data = GlobalConfigData.LoadSavedDataOrNew(GlobalConfigLocation);

            return field = new GlobalConfig
            {
                ActiveGame =
                    data.ActiveGameSlug is { } gameSlug
                    && Game.NameToGame.TryGetValue(gameSlug, out var game)
                        ? game
                        : null,
            };
        }
    }

    public Game? ActiveGame { get; set; }

    public static void Save()
    {
        GlobalConfigData data = new(Instance.ActiveGame?.Slug);
        data.Save(GlobalConfigLocation);
    }
}

public sealed class Game
{
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

        public string? PreferredPath { get; set; }

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

            field = GameConfig.LoadSavedDataOrNew(GameConfigLocation, JsonGen.Default.GameConfig);
            field.ConnectGame(this);
            return field;
        }
    }

    [JsonIgnore]
    public string GameConfigLocation =>
        field ??= Path.Combine(CogworkPaths.GetGamesSubDirectory(this), "config.json");

    [JsonIgnore]
    public IModInstallRules InstallRules { get; }
    public UserSource DefaultSource { get; }

    internal Dictionary<string, LazyModList> IdToModList { get; } = [];
    internal readonly Lock idToModListLock = new();

    internal Game(
        string name,
        string slug,
        IModInstallRules installRules,
        UserSource defaultSource = default
    )
    {
        Name = name;
        Slug = slug;
        InstallRules = installRules;
        if (defaultSource == default)
        {
            defaultSource = new UserSource(
                ThunderstoreCommunity.CreateDefault(this),
                SourceDominanceEntry.Always,
                SourceDominanceStrategy.ByHighestAvailableVersion,
                Visible: true
            );
        }
        DefaultSource = defaultSource;
    }

    public static Game Silksong { get; } =
        new("Hollow Knight: Silksong", "hollow-knight-silksong", new BepInExModInstallRules())
        {
            Platforms = new() { Steam = new() { Id = 1030300 } },
        };

    public static Game LethalCompany { get; } =
        new("Lethal Company", "lethal-company", new BepInExModInstallRules())
        {
            Platforms = new() { Steam = new() { Id = 1966720 } },
        };

    public static Game Repo { get; } =
        new("R.E.P.O.", "repo", new BepInExModInstallRules())
        {
            Platforms = new() { Steam = new() { Id = 3241660 } },
        };

    // public static Game Test { get; } =
    //     new("Test", "test", new BepInExModInstallRules(), new TestPackageSource())
    //     {
    //         Platforms = new(),
    //     };
    public static Game Ror2 { get; } =
        new("Risk of Rain 2", "riskofrain2", new BepInExModInstallRules())
        {
            Platforms = new() { Steam = new() { Id = 632360 } },
        };

    public static List<Game> SupportedGames { get; } =
    [
        Silksong,
        LethalCompany,
        Repo,
        Ror2,
#if DEBUG
        // Test,
#endif
    ];

    public static Dictionary<string, Game> NameToGame
    {
        get
        {
            if (field is { })
                return field;

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

            return field = dict;
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
            if (ModList.TryGetFromId(this, profileDir.Name, out var profile))
            {
                yield return profile;
            }
        }
    }
}
