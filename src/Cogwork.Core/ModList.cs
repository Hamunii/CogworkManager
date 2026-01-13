using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public sealed class ModList
{
    public sealed class ModListConfig : ISaveWithJson
    {
        public readonly record struct ServiceUrl(Uri Url);

        private ModList? _modList;

        [JsonIgnore]
        public PackageSourceIndex SourceIndex { get; set; } = null!;
        public string? DisplayName
        {
            get => _modList?.DisplayName ?? field;
            set
            {
                if (_modList is null || value is null)
                {
                    field = value;
                    return;
                }

                _modList.DisplayName = value;
            }
        }

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
                if (_modList is null || value is null)
                {
                    field = value ?? [];
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
                if (_modList is null || value is null)
                {
                    field = value ?? [];
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

        /// <summary>
        /// Connects a <see cref="ModList"/> and a <see cref="ModListConfig"/>
        /// together so their data is linked.
        /// </summary>
        internal void ConnectModListIfNeeded(ModList modList, bool existed = true)
        {
            if (_modList is { })
            {
                return;
            }

            if (!existed)
            {
                _modList = modList;
                SourceIndex = _modList.SourceIndex;
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

    static Dictionary<string, ModList> IdToModList { get; } = [];
    static readonly Lock idToModListLock = new();

    [JsonInclude]
    public ModListConfig Config
    {
        get
        {
            if (field is { })
                return field;

            field = ModListConfig.LoadSavedData(FileLocation);
            return field;
        }
        private set;
    }
    public string DisplayName { get; set; }
    public string Id { get; set; }
    public string DisambiguatedDisplayName
    {
        get => field ?? DisplayName;
        set;
    }

    public PackageSourceIndex SourceIndex { get; set; }

    [JsonIgnore]
    public string FileLocation => field ??= GetProfileFileLocation(_game, Id);

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Added { get; private set; } = [];

    [JsonIgnore]
    public Dictionary<Package, PackageVersion> Dependencies { get; private set; } = [];

    readonly Game _game;

    private ModList(
        Game game,
        string name,
        string? profileId,
        PackageSourceIndex sourceIndex,
        ModListConfig? config
    )
    {
        _game = game;
        DisplayName = name;

        if (profileId is { })
        {
            Id = profileId;
        }
        else
        {
            Span<char> id = new char[name.Length];
            name.CopyTo(id);
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                id.Replace(c, '_');
            }

            profileId = id.ToString();
            var profilesDir = CogworkPaths.GetProfilesSubDirectory(_game, "");
            var wouldBePath = Path.Combine(profilesDir, profileId);
            int num = 1;
            while (File.Exists(wouldBePath))
            {
                num++;
                wouldBePath = Path.Combine(profilesDir, Id + num);
            }
            if (num == 1)
                Id = profileId;
            else
                Id = profileId + num;
        }

        SourceIndex = sourceIndex;

        if (config is { })
        {
            Config = config;
        }
        else
        {
            // Init config. This is kinda bad.
            _ = Config;
        }
    }

    /// <summary>
    /// Constructs a new mod profile which has not existed before.
    /// </summary>
    public ModList(Game game, string name, PackageSourceIndex sourceIndex)
        : this(game, name, profileId: null, sourceIndex, config: null) { }

    public static string GetProfileFileLocation(Game game, string id) =>
        Path.Combine(CogworkPaths.GetProfilesSubDirectoryNoCreate(game, id), "profile.json");

    /// <summary>
    /// Gets ModList from id or returns null if it doesn't exist.
    /// </summary>
    public static ModList? GetFromId(Game game, string profileId)
    {
        lock (idToModListLock)
        {
            if (IdToModList.TryGetValue(profileId, out var modList))
                return modList;

            var path = GetProfileFileLocation(game, profileId);
            if (!File.Exists(path))
                return null;

            var config = ModListConfig.LoadSavedData(path);
            // TODO: Parse the PackageSourceIndex from the config
            // and actually give it to the ModList.
            modList = new ModList(game, config.DisplayName ?? profileId, profileId, null!, config);
            IdToModList.Add(profileId, modList);
            return modList;
        }
    }

    public void Add(Package package) => Add(package.Latest);

    public void Add(PackageVersion package)
    {
        Config.ConnectModListIfNeeded(this);
        _ = Added.AddOrUpdateToHigherVersion(package);
        RebuildDependencies();
        Config.Save(FileLocation);
    }

    public void Remove(Package package)
    {
        Config.ConnectModListIfNeeded(this);
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
        Config.ConnectModListIfNeeded(this);

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
