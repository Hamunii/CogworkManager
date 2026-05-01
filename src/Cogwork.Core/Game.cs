using System.IO.Abstractions;
using System.Text.Json.Serialization;
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
    public PackageSource? DefaultSource { get; }

    internal Game(
        string name,
        string slug,
        IModInstallRules installRules,
        PackageSource? defaultSource = null
    )
    {
        Name = name;
        Slug = slug;
        InstallRules = installRules;
        DefaultSource = defaultSource ?? new(new ThunderstoreCommunity(this));
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
        new("R.E.P.O.", "repo", new BepInExModInstallRules()) { Platforms = new() };
    public static Game Test { get; } =
        new("Test", "test", new BepInExModInstallRules(), new(new TestPackageSource()))
        {
            Platforms = new(),
        };
    public static Game Ror2 { get; } =
        new("Risk of Rain 2", "risk-of-rain-2", new BepInExModInstallRules()) { Platforms = new() };

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
            if (ModList.TryGetFromId(this, profileDir.Name, out var profile))
            {
                yield return profile;
            }
        }
    }
}
