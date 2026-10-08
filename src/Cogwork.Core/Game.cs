using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.Net;
using System.Text.Json.Serialization;
using Cogwork.Core.Installers;
using Cogwork.Core.Sources;
using DBusGenerated.Freedesktop.Portal;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Tmds.DBus.Protocol;
using Xdg.Directories;
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

    public IEnumerable<Game.Platform> GetAvailablePlatforms()
    {
        if (Steam is { })
            yield return Game.Platform.Steam;
    }
}

public readonly record struct GlobalConfigData(string? ActiveGameSlug, string? SteamDirectory)
    : ISaveWithJson;

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
                SteamDirectory = data.SteamDirectory,
            };
        }
    }

    public required Game? ActiveGame { get; set; }
    public required string? SteamDirectory { get; set; }

    public static void Save()
    {
        GlobalConfigData data = new(Instance.ActiveGame?.Slug, Instance.SteamDirectory);
        data.Save(GlobalConfigLocation);
    }
}

public static class LaunchConfigExtensions
{
    public static (Game.GameLaunchRequest, CogError?) CreateLaunchRequest(
        this Game.LaunchConfig? self,
        Game game
    )
    {
        if (self is not { } config)
            return (Game.GameLaunchRequest.CreateDefault(game), null);

        if (
            !Game.GameLaunchRequest.TryCreate(
                game,
                config.Platform,
                config.LaunchType,
                out var request
            )
        )
            return (
                default,
                new(
                    $"Failed to launch game",
                    $"{game.Name} is not available on {config.Platform}.",
                    "Try setting another launch platform."
                )
            );

        return (request, null);
    }
}

public sealed class Game
{
    public readonly record struct LaunchConfig(Platform Platform, LaunchType LaunchType);

    public readonly record struct GameConfigData(
        string? ActiveProfileId,
        string? PreferredPath,
        LaunchConfig? LaunchConfig
    ) : ISaveWithJson;

    public sealed class GameConfig
    {
        public required Game Game { private get; init; }
        public required LazyModList? ActiveProfile { get; set; }
        public required string? PreferredPath { get; set; }
        public required LaunchConfig? LaunchConfig { get; set; }

        public void Save()
        {
            GameConfigData data = new(ActiveProfile?.Id, PreferredPath, LaunchConfig);
            data.Save(Game.GameConfigLocation);
        }

        public async Task<CogError?> PopulateGamePathIfNotValidAsync()
        {
            var gamePath = PreferredPath;
            if (gamePath is not null && Directory.Exists(gamePath))
            {
                return null;
            }

            var (steamPath, steamError) = GetSteamDirectory();
            if (steamError is { })
                return steamError;

            var ((manifestPath, manifestFileName), manifestError) =
                await FindAppManifestLocationAsync(steamPath!, Game);
            if (manifestError is { })
                return manifestError;

            var gameInfoAcf = Path.Combine(manifestPath!, manifestFileName!);
            var appmanifest = VdfConvert.Deserialize(File.ReadAllText(gameInfoAcf));
            var installDir = appmanifest.Value["installdir"]?.Value<string>();
            if (installDir is null)
            {
                return new(
                    "Unable to find game install",
                    $"Steam install directory not found for: '{Game.Name}'",
                    "Please define the game root path in Cogwork settings."
                );
            }

            gamePath = Path.Combine(gameInfoAcf, "common", installDir);
            if (!Directory.Exists(gamePath))
            {
                return new(
                    "Game install not found",
                    $"Steam directory does not exist: '{gamePath}'",
                    "Try verifying integrity of game files in Steam"
                        + " or define the game root path in Cogwork settings."
                );
            }

            PreferredPath = gamePath;
            Save();
            return null;
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
                LaunchConfig = data.LaunchConfig,
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
        new("Balatro", "balatro", LovelyInstallers.Default)
        {
            Platforms = new() { Steam = new() { Id = 2379780 } },
        };

    public static List<Game> SupportedGames { get; } =
    [
        Silksong,
        LethalCompany,
        Repo,
        Ror2,
        Balatro,
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

    public static bool IsGamePathValid(string gamePath, [NotNullWhen(false)] out string? err)
    {
        if (!Directory.Exists(gamePath))
        {
            err = $"Directory not found";
            return false;
        }

        err = null;
        return true;
    }

    public enum Platform
    {
        Steam,
    }

    public enum LaunchType
    {
        Platform,
        Direct,
    }

    public readonly record struct GameLaunchRequest
    {
        public readonly Game Game { get; private init; }
        public readonly Platform Platform { get; private init; }
        public readonly LaunchType Launch { get; private init; }
        public readonly string PlatformGameId { get; private init; }

        public static GameLaunchRequest CreateDefault(Game game)
        {
            var launchType = LaunchType.Platform;
            if (TryCreate(game, Platform.Steam, launchType, out var request))
                return request;

            throw new UnreachableException(
                "There should always be at least one available platform for a game."
            );
        }

        public static bool TryCreate(
            Game game,
            Platform platform,
            LaunchType launch,
            out GameLaunchRequest request
        )
        {
            var id = platform switch
            {
                Platform.Steam => game.Platforms.Steam?.Id.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (id is null)
            {
                request = default;
                return false;
            }

            request = new()
            {
                Game = game,
                Platform = platform,
                Launch = launch,
                PlatformGameId = id,
            };
            return true;
        }
    }

    public static async Task<CogError?> LaunchGame(LazyModList modList, GameLaunchRequest request)
    {
        var game = request.Game;
        if (DBusAddress.Session is null)
            return new(
                "No D-Bus session found",
                "No D-Bus session found.",
                "Install D-Bus to solve this issue."
            );

        using var connection = new DBusConnection(DBusAddress.Session);
        await connection.ConnectAsync();

        var openUri = new OpenURI(
            connection,
            "org.freedesktop.portal.Desktop",
            "/org/freedesktop/portal/desktop"
        );

        var prepareError = await modList.PrepareModLoaderAsync(game);
        if (prepareError is { })
            return prepareError;

        var isLinuxApp = modList.IsLinuxNative();

        if (request.Launch is LaunchType.Direct)
        {
            var directArgs = game.InstallRules.GetDirectLaunchArgs(modList);
            var args = game.InstallRules.GetLaunchArgs(modList);

            var (gamePath, err) = modList.GetGamePath();
            if (err is { })
                return err;

            ProcessStartInfo startInfo;
            if (isLinuxApp)
            {
                var setsid = Utils.GetExecutablePath("setsid");
                if (setsid is null)
                    return new("setsid not found");

                var sh = Utils.GetExecutablePath("sh");
                if (sh is null)
                    return new("sh not found");

                startInfo = new(setsid, [sh, .. directArgs, .. args]);
            }
            else
            {
                var umuRun = Utils.GetExecutablePath("umu-run");
                if (umuRun is null)
                    return new("umu-run not found");

                startInfo = new(umuRun, [.. directArgs, .. args]);

                KeyValuePair<string, string> env = new("WINEDLLOVERRIDES", "winhttp=n,b");
                startInfo.Environment[env.Key] = env.Value;
                Cog.Information($"Env: '{env}'");
            }
            startInfo.WorkingDirectory = gamePath;

            Cog.Information(
                $"Arguments: '{startInfo.FileName}' '{string.Join("' '", startInfo.ArgumentList)}'"
            );

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new("Game process could not be started.");
            }
            return null;
        }

        switch (request.Platform)
        {
            case Platform.Steam:
                var error = await EnsureWineWillLoadDllOverrideAsync(
                    game,
                    game.InstallRules.GetProxyFiles()
                );
                if (error is { })
                    return error;

                var args = game.InstallRules.GetLaunchArgs(modList);
                var argString = $"\"{string.Join("\" \"", args)}\"";
                var argEncoded = WebUtility.UrlEncode(argString);
                var argEncodedFixed = argEncoded.Replace("+", "%20");
                var launchUri = $"steam://run/{request.PlatformGameId}//{argEncodedFixed}/";

                Cog.Information($"Attempting to launch: '{launchUri}'");
                try
                {
                    await openUri.OpenURIAsync(string.Empty, launchUri, []);
                }
                catch (Exception ex)
                {
                    return new("Game failed to launch", ex.Message, null);
                }
                return null;

            default:
                return new("Not supported", "Only steam is supported so far.", null);
        }
    }

    // The following code is largely from r2modman:
    // https://github.com/ebkr/r2modmanPlus/blob/a1897e3d/src/r2mm/launching/runners/linux/SteamGameRunner_Linux.ts#L152

    static async Task<CogError?> EnsureWineWillLoadDllOverrideAsync(Game game, string[] proxyFiles)
    {
        if (proxyFiles.Length == 0)
            return null;

        var (compatDataDir, compatError) = await GetCompatDataDirectoryAsync(game);
        if (compatError is { })
            return compatError;

        string userReg = Path.Combine(compatDataDir!, "pfx", "user.reg");
        string userRegData = await File.ReadAllTextAsync(userReg);

        string ensuredUserRegData = userRegData;
        foreach (var proxy in proxyFiles)
        {
            ensuredUserRegData = RegAddInSection(
                ensuredUserRegData,
                "[Software\\\\Wine\\\\DllOverrides]",
                proxy,
                "native,builtin"
            );
        }

        if (userRegData != ensuredUserRegData)
        {
            string backupPath = Path.Combine(Path.GetDirectoryName(userReg)!, "user.reg.bak");
            File.Copy(userReg, backupPath, overwrite: true);

            await File.WriteAllTextAsync(userReg, ensuredUserRegData);
        }

        return null;
    }

    static string RegAddInSection(string reg, string section, string key, string value)
    {
        /*
            Example section
            [header]                // our section variable
            #time=...               // timestamp
            "key"="value"

            It's ended with two newlines (/n/n)
        */
        var split = reg.Split('\n');

        var begin = 0;
        // Get section begin
        for (var index = 0; index < split.Length; index++)
        {
            if (split[index].StartsWith(section, StringComparison.Ordinal))
            {
                begin = index + 2; // We need to skip the timestamp line
                break;
            }
        }

        // Get end
        var end = 0;
        for (var index = begin; index < split.Length; index++)
        {
            if (split[index].Length == 0)
            {
                end = index;
                break;
            }
        }

        // Check for key and fix it eventually, then return
        for (var index = begin; index < end; index++)
        {
            if (split[index].StartsWith($"\"{key}\"", StringComparison.Ordinal))
            {
                split[index] = $"\"{key}\"=\"{value}\"";
                return string.Join('\n', split);
            }
        }

        // Append key and return
        var list = split.ToList();
        list.Insert(end, $"\"{key}\"=\"{value}\"");
        return string.Join('\n', split);
    }

    public static (string? Result, CogError? Error) GetSteamDirectory()
    {
        var existingSteamDir = GlobalConfig.Instance.SteamDirectory;

        // 2. Check if a pre-existing directory configuration exists
        if (existingSteamDir is { } && Directory.Exists(existingSteamDir))
        {
            return (existingSteamDir, null);
        }

        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] dirs =
        [
            Path.Combine(BaseDirectory.DataHome, "Steam"),
            Path.Combine(homeDir, ".steam", "steam"),
            Path.Combine(homeDir, ".steam", "root"),
            Path.Combine(homeDir, ".steam"),
            Path.Combine(
                homeDir,
                ".var",
                "app",
                "com.valvesoftware.Steam",
                ".local",
                "share",
                "Steam"
            ),
            Path.Combine(homeDir, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam"),
            Path.Combine(homeDir, ".var", "app", "com.valvesoftware.Steam", ".steam", "root"),
            Path.Combine(homeDir, ".var", "app", "com.valvesoftware.Steam", ".steam"),
        ];

        foreach (var dir in dirs)
        {
            if (Directory.Exists(dir))
            {
                var files = Directory.EnumerateFiles(dir);
                bool hasSteamSh = files.Any(f =>
                    string.Equals(
                        Path.GetFileName(f),
                        "steam.sh",
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                if (hasSteamSh)
                {
                    return (dir, null);
                }
            }
        }

        var err = new CogError(
            "Unable to resolve Steam install folder",
            "Steam is not installed",
            "Try manually setting the Steam folder through the settings"
        );
        return (null, err);
    }

    public static async Task<(string? Result, CogError? Error)> GetCompatDataDirectoryAsync(
        Game game
    )
    {
        var (steamPath, steamError) = GetSteamDirectory();
        if (steamError is { })
            return (null, steamError);

        var ((manifestLocation, _), manifestError) = await FindAppManifestLocationAsync(
            steamPath!,
            game
        );
        if (manifestError is { })
            return (null, manifestError);

        string compatDataPath = Path.Combine(
            manifestLocation!,
            "compatdata",
            game.Platforms.Steam!.Value.Id.ToString(CultureInfo.InvariantCulture)
        );

        if (Directory.Exists(compatDataPath))
            return (compatDataPath, null);

        var fileNotFoundError = new CogError(
            $"{game.Name} compatibility data does not exist in Steam's specified location",
            $"Failed to find folder: {compatDataPath}",
            "If this happened, it is very likely that you did not start the game at least once. Please do it."
        );

        return (null, fileNotFoundError);
    }

    static async Task<(
        (string? path, string? fileName),
        CogError? Error
    )> FindAppManifestLocationAsync(string steamPath, Game game)
    {
        string[] probableSteamAppsLocations =
        [
            Path.Combine(steamPath, "steamapps"),
            Path.Combine(steamPath, "steam", "steamapps"),
            Path.Combine(steamPath, "root", "steamapps"),
        ];

        string? steamapps = null;
        foreach (var dir in probableSteamAppsLocations)
        {
            if (Directory.Exists(dir))
            {
                steamapps = Path.GetFullPath(dir);
                break;
            }
        }

        if (steamapps is null)
        {
            return (
                (null, null),
                new CogError(
                    "An error occurred whilst searching Steam library locations",
                    "Cannot define the root steamapps location",
                    null
                )
            );
        }

        List<string> locations = [steamapps];
        string libraryFoldersPath = Path.Combine(steamapps, "libraryfolders.vdf");

        if (File.Exists(libraryFoldersPath))
        {
            string fileContent = await File.ReadAllTextAsync(libraryFoldersPath);

            VProperty root;
            try
            {
                root = VdfConvert.Deserialize(fileContent);
            }
            catch (Exception ex)
            {
                return ((null, null), new("Unable to parse libraryfolders.vdf", ex.Message, null));
            }

            if (root?.Value is VObject libraryFolders)
            {
                foreach (var prop in libraryFolders)
                {
                    if (!int.TryParse(prop.Key, out _))
                        continue;

                    if (prop.Value is VObject folderObj && folderObj["path"] is { } path)
                    {
                        locations.Add(Path.Combine(path.ToString(), "steamapps"));
                    }
                    else if (prop.Value is { } propValue)
                    {
                        locations.Add(Path.Combine(propValue.ToString(), "steamapps"));
                    }
                }
            }
        }

        // Look through resolved paths for the target manifest file
        string? manifestLocation = null;
        string targetManifestFilename =
            $"appmanifest_{game.Platforms.Steam!.Value.Id.ToString(CultureInfo.InvariantCulture)}.acf";

        foreach (var location in locations)
        {
            if (!Directory.Exists(location))
                continue;

            var manifestFiles = Directory.EnumerateFiles(location);
            bool hasManifest = manifestFiles.Any(f =>
                string.Equals(
                    Path.GetFileName(f),
                    targetManifestFilename,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (hasManifest)
            {
                manifestLocation = location;
                break;
            }
        }

        if (manifestLocation is null)
        {
            string searchedPathsString = string.Join(", ", locations);
            return (
                (null, null),
                new CogError(
                    $"Unable to locate {game.Name} Installation Folder",
                    $"Searched locations: {searchedPathsString}",
                    null
                )
            );
        }

        return ((manifestLocation, targetManifestFilename), null);
    }
}

public readonly record struct CogError(string Name, string? Message, string? Solution)
{
    public CogError(string Name)
        : this(Name, null, null) { }
}
