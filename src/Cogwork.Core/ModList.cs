using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core;

public sealed class ModList
{
    public sealed class ModListConfig : ISaveWithJson
    {
        public readonly record struct ServiceUri(Uri Uri);

        private ModList? _modList;

        [JsonIgnore]
        public PackageSourceIndex? SourceIndex { get; set; }
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
        public IEnumerable<ServiceUri> Sources
        {
            get =>
                SourceIndex?.Sources.Select(x => new ServiceUri(x.Service.Uri)).Distinct()
                ?? field
                ?? [];
            set => field = value;
        }

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
                    if (Package.TryGetPackageVersion(SourceIndex, package, out var packageVersion))
                        _modList.Added.Add(packageVersion.Package, packageVersion);
                    else
                        Lost.Add(package);
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
                    // Since these are dependencies, we don't care if some are not found, I think.
                    if (Package.TryGetPackageVersion(SourceIndex, package, out var packageVersion))
                        _modList.Dependencies.Add(packageVersion.Package, packageVersion);
                }
            }
        } = null!;

        [JsonInclude]
        [JsonPropertyName("Lost")]
        public List<string> Lost
        {
            get;
            set => field = value ?? field;
        } = [];

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

            field = ModListConfig.LoadSavedData(
                FileLocation,
                SourceGenerationContext.Default.ModListConfig
            );
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

    [JsonIgnore]
    public IEnumerable<KeyValuePair<Package, PackageVersion>> AllPackages =>
        Added.Concat(Dependencies);

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
            // This also magically initializes config. This is kinda bad.
            // We make sure the just-created profile is saved.
            Config!.ConnectModListIfNeeded(this, existed: false);
            Config.Save(FileLocation!, SourceGenerationContext.Default.ModListConfig);
        }
    }

    /// <summary>
    /// Constructs a new mod profile which has not existed before.
    /// </summary>
    public static LazyModList Create(Game game, string name, PackageSourceIndex sourceIndex)
    {
        var modList = new ModList(game, name, profileId: null, sourceIndex, config: null);
        return new(modList);
    }

    public static string GetProfileFileLocation(Game game, string id) =>
        Path.Combine(CogworkPaths.GetProfilesSubDirectoryNoCreate(game, id), "profile.json");

    /// <summary>
    /// Gets ModList from id or returns null if it doesn't exist.
    /// </summary>
    public static LazyModList? GetFromId(Game game, string profileId)
    {
        lock (idToModListLock)
        {
            if (IdToModList.TryGetValue(profileId, out var modList))
                return new(modList);

            var path = GetProfileFileLocation(game, profileId);
            if (!File.Exists(path))
                return null;

            var config = ModListConfig.LoadSavedData(
                path,
                SourceGenerationContext.Default.ModListConfig
            );
            // TODO: properly parse the PackageSourceIndex from the config.
            modList = new ModList(
                game,
                config.DisplayName ?? profileId,
                profileId,
                config.SourceIndex ?? new(game.DefaultSource),
                config
            );
            IdToModList.Add(profileId, modList);
            return new(modList);
        }
    }

    public bool Add(IEnumerable<Package> packages) => Add(packages.Select(x => x.Latest));

    public bool Add(IEnumerable<PackageVersion> packages)
    {
        Config.ConnectModListIfNeeded(this);
        bool updated = false;
        foreach (var package in packages)
        {
            updated |= Added.AddOrUpdateToHigherVersion(package);
        }
        RebuildDependencies();
        Config.Save(FileLocation, SourceGenerationContext.Default.ModListConfig);
        return updated;
    }

    public bool Add(Package package) => Add(package.Latest);

    public bool Add(PackageVersion package)
    {
        Config.ConnectModListIfNeeded(this);
        var updated = Added.AddOrUpdateToHigherVersion(package);
        RebuildDependencies();
        Config.Save(FileLocation, SourceGenerationContext.Default.ModListConfig);
        return updated;
    }

    public void Remove(IEnumerable<Package> packages)
    {
        Config.ConnectModListIfNeeded(this);
        foreach (var package in packages)
        {
            if (Added.Remove(package, out var packageVersion))
            {
                // Add this version temporarily to deps so version can't get downgraded on rebuild
                Dependencies.Add(package, packageVersion);
            }
        }
        RebuildDependencies();
        Config.Save(FileLocation, SourceGenerationContext.Default.ModListConfig);
    }

    public void Remove(Package package)
    {
        Config.ConnectModListIfNeeded(this);
        if (Added.Remove(package, out var packageVersion))
        {
            Dependencies.Add(package, packageVersion);
        }
        RebuildDependencies();
        Config.Save(FileLocation, SourceGenerationContext.Default.ModListConfig);
    }

    public void RebuildDependencies()
    {
        Dictionary<Package, PackageVersion> map = [];

        // Pass 1: collect highest available package versions to map.
        foreach (var added in Added)
        {
            added.Value.CollectAllDependenciesToMap(map);
        }

        // If any existing dependency is higher version than would be transitively from Added,
        // we want to keep those versions.
        foreach (var dependency in Dependencies)
        {
            dependency.Value.CollectAllDependenciesToMap(map);
        }

        Dictionary<Package, PackageVersion> allDependencies = [];

        // Pass 2: use the map to collect only dependencies of packages with highest versions.
        foreach (var added in Added)
        {
            added.Value.CollectAllDependenciesToDestination(map, allDependencies);
        }

        foreach (var added in Added)
        {
            allDependencies.Remove(added.Key);
        }

        Dependencies = allDependencies;
    }

    public void UpdatePackages()
    {
        foreach (var dependency in Dependencies)
        {
            Dependencies[dependency.Key] = dependency.Key.Latest;
        }
        Add(Added.Keys.Select(x => x.Latest));
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

public readonly record struct LazyModList
{
    public readonly string DisplayName => _modList.DisplayName;
    public readonly string Id => _modList.Id;
    public readonly string DisambiguatedDisplayName
    {
        get => _modList.DisambiguatedDisplayName;
        set => _modList.DisambiguatedDisplayName = value;
    }

    readonly ModList _modList;

    internal LazyModList(ModList modList)
    {
        _modList = modList;
    }

    public readonly async Task<ModList> LoadAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        _modList.SourceIndex.Import(_modList.Config.Sources.Select(x => x.Uri));

        // Initialize package data
        _ = await _modList.SourceIndex.GetAllPackagesAsync(progressFactory);

        _modList.Config.ConnectModListIfNeeded(_modList);
        return _modList;
    }

    public readonly ModList GetAndBypassLoadWithRiskOfBugs() => _modList;
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

        if (package.Version.IsHigherThan(value!.Version))
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

        if (package.Version.IsHigherThan(value.Version))
        {
            return package;
        }

        return value;
    }
}
