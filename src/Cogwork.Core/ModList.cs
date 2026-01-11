using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public class ModList
{
    public class ModListConfig : ISaveWithJson
    {
        public readonly record struct ServiceUrl(Uri Url);

        private ModList? _modList;

        [JsonIgnore]
        public PackageSourceIndex SourceIndex { get; set; } = null!;

        [JsonInclude]
        [JsonPropertyName("Sources")]
        public IEnumerable<ServiceUrl> Sources =>
            SourceIndex?.Sources.Select(x => new ServiceUrl(x.Service.Url)).Distinct() ?? [];

        [JsonInclude]
        [JsonPropertyName("Added")]
        public IEnumerable<string> AddedPackages
        {
            get => _modList?.Added.Values.Select(x => x.ToString()) ?? field;
            set
            {
                if (_modList is null)
                {
                    field = value;
                    return;
                }

                Debug.Assert(SourceIndex is { });

                foreach (var package in value)
                {
                    var packageVersion = Package.GetPackageVersion(SourceIndex, package);
                    _modList.Added.Add(packageVersion.Package, packageVersion);
                }
            }
        } = null!;

        [JsonInclude]
        [JsonPropertyName("Dependencies")]
        public IEnumerable<string> DependencyPackages
        {
            get => _modList?.Dependencies.Values.Select(x => x.ToString()) ?? field;
            set
            {
                if (_modList is null)
                {
                    field = value;
                    return;
                }

                Debug.Assert(SourceIndex is { });

                foreach (var package in value)
                {
                    var packageVersion = Package.GetPackageVersion(SourceIndex, package);
                    _modList.Dependencies.Add(packageVersion.Package, packageVersion);
                }
            }
        } = null!;

        internal void ConnectModList(ModList modList)
        {
            if (_modList is { })
            {
                Cog.Warning(
                    $"{nameof(ModListConfig)} already has "
                        + $"{nameof(_modList)} named '{_modList.Name}'"
                );
                return;
            }

            var added = AddedPackages;
            var dependencies = DependencyPackages;

            _modList = modList;
            SourceIndex = _modList.SourceIndex;

            AddedPackages = added;
            DependencyPackages = dependencies;
        }
    }

    [JsonInclude]
    public ModListConfig Config
    {
        get
        {
            if (field is { })
                return field;

            var config = field = ModListConfig.LoadSavedData(FileLocation);
            config.ConnectModList(this);
            return config;
        }
    }
    public string Name { get; set; }

    public PackageSourceIndex SourceIndex { get; set; }

    [JsonIgnore]
    public string FileLocation =>
        field ??= Path.Combine(CogworkPaths.GetDataSubDirectory(_game.Slug), Name, "mod-list.json");

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Added { get; private set; } = [];

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Dependencies { get; private set; } = [];

    readonly Game _game;

    public ModList(Game game, string name, PackageSourceIndex sourceIndex)
    {
        _game = game;
        Name = name;
        SourceIndex = sourceIndex;
        _ = Config;
    }

    public void Add(Package package) => Add(package.Latest);

    public void Add(PackageVersion package)
    {
        _ = Added.AddOrUpdateToHigherVersion(package);
        RebuildDependencies();
        Config.Save(FileLocation);
    }

    public void Remove(Package package)
    {
        Added.Remove(package);
        RebuildDependencies();
        Config.Save(FileLocation);
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
