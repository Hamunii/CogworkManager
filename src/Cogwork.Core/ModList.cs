using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public class ModList : ISaveWithJson
{
    public class ModListConfig
    {
        public readonly record struct HandlerUrl(Uri Url);

        [JsonIgnore]
        public PackageSourceIndex RepoList { get; init; } = null!;

        [JsonInclude]
        [JsonPropertyName("Repos")]
        public IEnumerable<HandlerUrl> RepoHandlers =>
            RepoList.Sources.Select(x => new HandlerUrl(x.Service.Url));
    }

    [JsonInclude]
    public ModListConfig Config { get; init; }

    [JsonIgnore]
    public string FileLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetDataSubDirectory(_game.Slug).FullName,
            _name,
            "mod-list.json"
        );

    [JsonInclude]
    [JsonPropertyName("Added")]
    public List<string> AddedPackages
    {
        get => [.. Added.Values.Select(x => x.ToString())];
        set =>
            value.ForEach(x =>
            {
                var packageVersion = Package.GetPackageVersion(Config.RepoList, x);
                Added.Add(packageVersion.Package, packageVersion);
            });
    }

    [JsonInclude]
    [JsonPropertyName("Dependencies")]
    public List<string> DependencyPackages
    {
        get => [.. Dependencies.Values.Select(x => x.ToString())];
        set =>
            value.ForEach(x =>
            {
                var packageVersion = Package.GetPackageVersion(Config.RepoList, x);
                Dependencies.Add(packageVersion.Package, packageVersion);
            });
    }

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Added { get; private set; } = [];

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Dependencies { get; private set; } = [];

    readonly Game _game;
    readonly string _name;

    public ModList(Game game, string name, ModListConfig config)
    {
        _game = game;
        _name = name;
        Config = config;
    }

    public void Add(Package package) => Add(package.Latest);

    public void Add(PackageVersion package)
    {
        _ = Added.AddOrUpdateToHigherVersion(package);
        RebuildDependencies();
        this.Save(FileLocation);
    }

    public void Remove(Package package)
    {
        Added.Remove(package);
        RebuildDependencies();
        this.Save(FileLocation);
    }

    public void RebuildDependencies()
    {
        Dictionary<Package, PackageVersion> map = [];

        // Pass 1: collect highest available package versions to map.
        foreach (var added in Added)
        {
            added.Value.CollectAllDependenciesToMap(map);
        }

        Dictionary<Package, PackageVersion> allDependencies = [];

        // Pass 2: use the map to collect only dependencies of packages with highest versions.
        foreach (var added in Added)
        {
            var higher = map.GetHigherVersion(added.Value);
            higher.CollectAllDependenciesToDestination(map, allDependencies);
        }

        foreach (var added in Added)
        {
            allDependencies.Remove(added.Key);
        }

        Dependencies = allDependencies;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Added:");
        foreach (var added in Added.Values)
        {
            sb.Append("  ");
            sb.AppendLine(added.ToString());
        }

        sb.AppendLine("Dependencies:");
        foreach (var dependency in Dependencies.Values)
        {
            sb.Append("  ");
            sb.AppendLine(dependency.ToString());
        }

        return sb.ToString();
    }

    // void Save()
    // {
    //     JsonSerializerOptions options = new()
    //     {
    //         DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    //     };
    //     var serialized = JsonSerializer.Serialize(this, options);
    //     _ = Directory.CreateDirectory(Path.GetDirectoryName(FileLocation)!);
    //     File.WriteAllText(FileLocation, serialized);
    // }
}

public static class ModListExtensions
{
    /// <summary>
    /// Adds <paramref name="package"/> to the provided <paramref name="dictionary"/>.
    /// If the <paramref name="package"/> already exists in the <paramref name="dictionary"/>,
    /// then it's only updated if the value is higher than the existing value.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if value was added or updated; otherwise <see langword="false"/>.
    /// </returns>
    public static bool AddOrUpdateToHigherVersion(
        this Dictionary<Package, PackageVersion> dictionary,
        PackageVersion package
    )
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary,
            package.Package,
            out var exists
        );

        if (!exists)
        {
            value = package;
            return true;
        }

        if (package.Version > value!.Version)
        {
            value = package;
            return true;
        }

        return false;
    }

    public static PackageVersion GetHigherVersion(
        this Dictionary<Package, PackageVersion> dictionary,
        PackageVersion package
    )
    {
        if (!dictionary.TryGetValue(package.Package, out var value))
        {
            return package;
        }

        if (package.Version > value.Version)
        {
            return package;
        }

        return value;
    }
}
