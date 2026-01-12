using Xdg.Directories;

namespace Cogwork.Core;

public static class CogworkPaths
{
    const string appId = "Hamunii.Cogwork";

    public static string CacheDirectory => CombineAndCreate(BaseDirectory.CacheHome, appId);
    public static string DataDirectory => CombineAndCreate(BaseDirectory.DataHome, appId);

    public static string GetCacheSubDirectory(string subDirectory) =>
        CombineAndCreate(CacheDirectory, subDirectory);

    public static string GetDataSubDirectory(string subDirectory) =>
        CombineAndCreate(DataDirectory, subDirectory);

    public static string GetGamesSubDirectory(Game game) =>
        CombineAndCreate(DataDirectory, "games", game.Slug);

    public static string GetGamesSubDirectory(Game game, string subDirectory) =>
        CombineAndCreate(DataDirectory, "games", game.Slug, subDirectory);

    public static string GetProfilesSubDirectoryNoCreate(Game game, string subDirectory) =>
        Path.Combine(GetGamesSubDirectory(game, "profiles"), subDirectory);

    public static string GetProfilesSubDirectory(Game game, string subDirectory) =>
        CombineAndCreate(GetGamesSubDirectory(game, "profiles"), subDirectory);

    static string CombineAndCreate(params string[] paths)
    {
        var path = Path.Combine(paths);
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
