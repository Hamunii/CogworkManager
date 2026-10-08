using System.Runtime.InteropServices;
using Xdg.Directories;

namespace Cogwork.Core;

public static class CogworkPaths
{
    internal const string AppId = "Hamunii.Cogwork";

    public static string CacheDirectory => CombineAndCreate(BaseDirectory.CacheHome, AppId);
    public static string DataDirectory => CombineAndCreate(BaseDirectory.DataHome, AppId);
    public static string HostDataDirectory
    {
        get
        {
            if (field is { })
                return field;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var hostDataDir = Environment.GetEnvironmentVariable("HOST_XDG_DATA_HOME");
                if (hostDataDir is { })
                    return field = Path.Combine(hostDataDir, AppId);

                var hostUser = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return field = Path.Combine(hostUser, ".local", "share", AppId);
            }
            return field = Path.Combine(BaseDirectory.DataHome, AppId);
        }
    }

    public static string GetCacheSubDirectory(string subDirectory) =>
        CombineAndCreate(CacheDirectory, subDirectory);

    public static string GetCacheIndexSubDirectory(string subDirectory) =>
        CombineAndCreate(CacheDirectory, "index", subDirectory);

    public static string GetCacheIndexSubDirectory(string subDirectory1, string subDirectory2) =>
        CombineAndCreate(CacheDirectory, "index", subDirectory1, subDirectory2);

    public static string GetDataSubDirectory(string subDirectory) =>
        CombineAndCreate(DataDirectory, subDirectory);

    public static string GetPackagesSubDirectory(string subDirectory) =>
        CombineAndCreate(DataDirectory, "packages", subDirectory);

    public static string GetPackagesSubDirectory(string subDirectory, string packageName) =>
        CombineAndCreate(DataDirectory, "packages", subDirectory, packageName);

    public static string GetGamesSubDirectory(Game game) =>
        CombineAndCreate(DataDirectory, "games", game.Slug);

    public static string GetProfilesDirectory(Game game) =>
        CombineAndCreate(DataDirectory, "games", game.Slug, "profiles");

    public static string GetHostProfilesDirectory(Game game) =>
        CombineAndCreate(HostDataDirectory, "games", game.Slug, "profiles");

    public static string GetProfilesSubDirectoryNoCreate(Game game, string subDirectory) =>
        Path.Combine(GetProfilesDirectory(game), subDirectory);

    public static string GetProfilesSubDirectory(Game game, string subDirectory) =>
        CombineAndCreate(GetProfilesDirectory(game), subDirectory);

    public static string CombineAndCreate(params ReadOnlySpan<string> paths)
    {
        var path = Path.Combine(paths);
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
