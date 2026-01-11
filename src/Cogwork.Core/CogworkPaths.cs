using Xdg.Directories;

namespace Cogwork.Core;

public static class CogworkPaths
{
    const string appId = "Hamunii.Cogwork";
    public static string CacheDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory.CacheHome, appId);
            _ = Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DataDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory.DataHome, appId);
            _ = Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string GetCacheSubDirectory(string subDirectory)
    {
        var path = Path.Combine(CacheDirectory, subDirectory);
        _ = Directory.CreateDirectory(path);
        return path;
    }

    public static string GetDataSubDirectory(string subDirectory)
    {
        var path = Path.Combine(DataDirectory, subDirectory);
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
