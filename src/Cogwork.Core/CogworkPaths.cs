using Xdg.Directories;

namespace Cogwork.Core;

public static class CogworkPaths
{
    const string appId = "Hamunii.Cogwork";
    public static DirectoryInfo CacheDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory.CacheHome, appId);
            var dir = Directory.CreateDirectory(path);
            return dir;
        }
    }

    public static DirectoryInfo DataDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory.DataHome, appId);
            var dir = Directory.CreateDirectory(path);
            return dir;
        }
    }

    public static DirectoryInfo GetCacheSubDirectory(string subDirectory)
    {
        var path = Path.Combine(CacheDirectory.FullName, subDirectory);
        var dir = Directory.CreateDirectory(path);
        return dir;
    }

    public static DirectoryInfo GetDataSubDirectory(string subDirectory)
    {
        var path = Path.Combine(DataDirectory.FullName, subDirectory);
        var dir = Directory.CreateDirectory(path);
        return dir;
    }
}
