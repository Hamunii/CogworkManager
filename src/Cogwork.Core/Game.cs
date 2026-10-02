using System.IO.Abstractions;
using System.Text.Json.Serialization;
using Cogwork.Core.Installers;
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

    public required Game? ActiveGame { get; set; }

    public static void Save()
    {
        GlobalConfigData data = new(Instance.ActiveGame?.Slug);
        data.Save(GlobalConfigLocation);
    }
}

public sealed class Game
{
    public readonly record struct GameConfigData(string? ActiveProfileId, string? PreferredPath)
        : ISaveWithJson;

    public sealed class GameConfig
    {
        public required Game Game { private get; init; }
        public required LazyModList? ActiveProfile { get; set; }
        public required string? PreferredPath { get; set; }

        public void Save()
        {
            GameConfigData data = new(ActiveProfile?.Id, PreferredPath);
            data.Save(Game.GameConfigLocation);
        }
    }

    public GameConfig Config
    {
        get
        {
            if (field is { })
                return field;

            var data = GameConfigData.LoadSavedDataOrNew(GameConfigLocation);

            return field = new GameConfig
            {
                Game = this,
                ActiveProfile =
                    data.ActiveProfileId is { } profileId
                    && ModList.GetFromId(this, profileId) is { } modList
                        ? modList
                        : null,
                PreferredPath = data.PreferredPath,
            };
        }
    }

    [JsonIgnore]
    public string GameConfigLocation =>
        field ??= Path.Combine(CogworkPaths.GetGamesSubDirectory(this), "config.json");

    [JsonIgnore]
    public IModInstallers InstallRules { get; }
    public UserSource DefaultSource { get; }

    internal Dictionary<string, LazyModList> IdToModList { get; } = [];
    internal readonly Lock idToModListLock = new();

    internal Game(
        string name,
        string slug,
        IModInstallers installRules,
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
        new("Hollow Knight: Silksong", "hollow-knight-silksong", BepInExInstallers.Default)
        {
            Platforms = new() { Steam = new() { Id = 1030300 } },
        };

    public static Game LethalCompany { get; } =
        new("Lethal Company", "lethal-company", BepInExInstallers.Default)
        {
            Platforms = new() { Steam = new() { Id = 1966720 } },
        };

    public static Game Repo { get; } =
        new("R.E.P.O.", "repo", BepInExInstallers.Default)
        {
            Platforms = new() { Steam = new() { Id = 3241660 } },
        };

    // public static Game Test { get; } =
    //     new("Test", "test", BepInExModInstallRules.Default, new TestPackageSource())
    //     {
    //         Platforms = new(),
    //     };
    public static Game Ror2 { get; } =
        new("Risk of Rain 2", "riskofrain2", BepInExInstallers.Default)
        {
            Platforms = new() { Steam = new() { Id = 632360 } },
        };

    public static Game Balatro { get; } =
        new("Balatro", "balatro", new LovelyInstaller())
        {
            Platforms = new() { Steam = new() { Id = 2379780 } },
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
