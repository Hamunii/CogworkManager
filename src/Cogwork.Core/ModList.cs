using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core;

public readonly record struct ServiceUri(Uri Uri);

public readonly record struct ModListData(
    string? DisplayName,
    IEnumerable<ServiceUri>? Sources,
    IEnumerable<string>? PackageIds
) : ISaveWithJson;

public readonly record struct ModListLockFile(
    IEnumerable<KeyValuePair<string, PackageVersionNumber>>? ResolvedAdded,
    IEnumerable<KeyValuePair<string, PackageVersionNumber>>? ResolvedDependencies
) : ISaveWithJson;

public sealed class LazyModList
{
    public required string DisplayName { get; init; }
    public string Id { get; }
    public string DisambiguatedDisplayName
    {
        get => field ?? DisplayName;
        set;
    }
    public required PackageSourceIndex SourceIndex { get; init; }
    public required Game Game { get; init; }
    public IEnumerable<string> AddedPackageIds { get; private set; }
    public Dictionary<string, PackageVersionNumber>? ResolvedAdded
    {
        get
        {
            if (_isResolvedAddedDirty)
            {
                field = new(
                    _modList!.Added.Select(x => new KeyValuePair<string, PackageVersionNumber>(
                        x.Key.ToStringSimpleWithSource(),
                        x.Value.Version
                    ))
                );
                _isResolvedAddedDirty = false;
            }
            return field;
        }
        private set;
    }

    public Dictionary<string, PackageVersionNumber>? ResolvedDependencies
    {
        get
        {
            if (_isResolvedDependenciesDirty)
            {
                field = new(
                    _modList!.Dependencies.Select(x => new KeyValuePair<
                        string,
                        PackageVersionNumber
                    >(x.Key.ToStringSimpleWithSource(), x.Value.Version))
                );
                _isResolvedDependenciesDirty = false;
            }
            return field;
        }
        private set;
    }
    public string ProfileSaveDataPath => field ??= ModList.GetProfileFileLocation(Game, Id);
    ModList? _modList;
    bool _isResolvedAddedDirty;
    bool _isResolvedDependenciesDirty;

    internal LazyModList(
        string profileId,
        IEnumerable<string>? addedPackageIds,
        ModListLockFile lockFile
    )
    {
        Id = profileId;
        AddedPackageIds = addedPackageIds ?? [];

        Cog.Verbose("Resolved packages:");

        if (lockFile.ResolvedAdded is { } resolvedAdded)
        {
            ResolvedAdded = new(resolvedAdded);

            foreach (var dep in ResolvedAdded.Select(x => new VisualPackageVersion(x)))
                Cog.Verbose(dep.ToString());
        }

        if (lockFile.ResolvedDependencies is { } resolvedDependencies)
        {
            ResolvedDependencies = new(resolvedDependencies);

            foreach (var dep in ResolvedDependencies.Select(x => new VisualPackageVersion(x)))
                Cog.Verbose(dep.ToString());
        }

        lock (ModList.idToModListLock)
        {
            ModList.IdToModList.Add(profileId, this);
        }
    }

    public async Task<ModList> LoadAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        await SourceIndex.FetchAllPackagesAsync(progressFactory);

        if (_modList is { })
            return _modList;

        _modList = new(
            this,
            onNewAddedDictionary: (modList, added) =>
                AddedPackageIds = added
                    .Select(x => x.Key.ToStringSimpleWithSource())
                    .Concat(modList.LostPackageIds),
            onResolved: () =>
            {
                _isResolvedAddedDirty = true;
                _isResolvedDependenciesDirty = true;
            }
        );

        return _modList;
    }

    public void SaveData()
    {
        ModListData modListData = new()
        {
            DisplayName = DisplayName,
            Sources = SourceIndex.Sources.Select(x => new ServiceUri(x.Service.Uri)),
            PackageIds = AddedPackageIds,
        };

        modListData.Save(ProfileSaveDataPath);
        ModListLockFile lockFile = new([], []);
        lockFile.Save(ProfilePackageLockPath);
    }

    public string ProfilePackageLockPath => field ??= GetProfilePackageLockPath(Game, Id);

    public static string GetProfilePackageLockPath(Game game, string id) =>
        Path.Combine(CogworkPaths.GetProfilesSubDirectoryNoCreate(game, id), "lock.json");
}

public sealed class ModList
{
    internal static readonly Lock idToModListLock = new();
    internal static Dictionary<string, LazyModList> IdToModList { get; } = [];
    public PackageSourceIndex SourceIndex => _lazy.SourceIndex;
    public Dictionary<Package, PackageVersion> Added { get; } = [];
    public Dictionary<Package, PackageVersion> Dependencies { get; private set; } = [];
    public IEnumerable<KeyValuePair<Package, PackageVersion>> AllPackages =>
        Added.Concat(Dependencies);
    public List<string> LostPackageIds { get; } = [];
    readonly LazyModList _lazy;
    readonly Action<ModList, Dictionary<Package, PackageVersion>> _onNewAddedList;
    readonly Action _onResolved;

    internal ModList(
        LazyModList lazyModList,
        Action<ModList, Dictionary<Package, PackageVersion>> onNewAddedDictionary,
        Action onResolved
    )
    {
        _lazy = lazyModList;
        List<PackageVersion> packages = [];

        foreach (var packageId in _lazy.AddedPackageIds)
        {
            if (Package.TryGetPackageWithNoVersion(_lazy.SourceIndex, packageId, out var package))
            {
                if (
                    _lazy.ResolvedAdded is { } resolved
                    && resolved.TryGetValue(
                        package.ToStringSimpleWithSource(),
                        out var packageVersionNumber
                    )
                    && package.TryGetVersion(packageVersionNumber, out var packageVersion)
                )
                {
                    Cog.Verbose($"Add PackageVersion {packageVersion}");
                    packages.Add(packageVersion);
                    continue;
                }

                Cog.Verbose($"Add Package (no resolved Version found) {package.Latest}");
                packages.Add(package.Latest);
            }
            else
            {
                LostPackageIds.Add(packageId);
            }
        }

        if (_lazy.ResolvedDependencies is { } resolvedDependencies)
        {
            foreach (var package in resolvedDependencies)
            {
                if (
                    Package.TryGetPackageVersion(
                        _lazy.SourceIndex,
                        new VisualPackageVersion(package).ToString(),
                        out var packageVersion
                    )
                )
                {
                    Dependencies.Add(packageVersion.Package, packageVersion);
                }
            }
        }

        _onNewAddedList = onNewAddedDictionary;
        _onResolved = onResolved;
        _onNewAddedList(this, Added);

        Add(packages);
    }

    public static LazyModList CreateNew(Game game, string name)
    {
        var modList = new LazyModList(GetUniqueProfileId(game, name), null, default)
        {
            Game = game,
            DisplayName = name,
            SourceIndex = game.DefaultSource is { } ? new(game.DefaultSource) : new(),
        };

        // Since this profile didn't exist previously, we should save it now so it stays.
        modList.SaveData();
        return modList;
    }

    private static string GetUniqueProfileId(Game game, string name)
    {
        Span<char> nameSpan = new char[name.Length];
        name.CopyTo(nameSpan);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            nameSpan.Replace(c, '_');
        }

        var safeName = nameSpan.ToString();
        var profilesDir = CogworkPaths.GetProfilesSubDirectory(game, "");
        var wouldBePath = Path.Combine(profilesDir, safeName);
        int num = 1;
        while (File.Exists(wouldBePath))
        {
            num++;
            wouldBePath = Path.Combine(profilesDir, safeName + num);
        }
        if (num > 1)
        {
            safeName += num;
        }

        return safeName;
    }

    public static LazyModList CreateFromData(
        Game game,
        string profileId,
        ModListData data,
        ModListLockFile lockFile
    )
    {
        var modList = new LazyModList(profileId, data.PackageIds, lockFile)
        {
            Game = game,
            DisplayName = data.DisplayName ?? profileId,
            SourceIndex =
                data.Sources is { } ? new(data.Sources.Select(x => x.Uri))
                : game.DefaultSource is { } ? new(game.DefaultSource)
                : new(),
        };

        return modList;
    }

    /// <summary>
    /// Gets ModList from id or returns null if it doesn't exist.
    /// </summary>
    public static LazyModList? GetFromId(Game game, string profileId)
    {
        lock (idToModListLock)
        {
            if (IdToModList.TryGetValue(profileId, out var modList))
                return modList;

            var path = GetProfileFileLocation(game, profileId);
            if (!File.Exists(path))
                return null;

            var data = ModListData.LoadSavedData(path, JsonGen.Default.ModListData);
            var lockFile = ModListLockFile.LoadSavedData(
                LazyModList.GetProfilePackageLockPath(game, profileId),
                JsonGen.Default.ModListLockFile
            );
            modList = CreateFromData(game, profileId, data, lockFile);
            return modList;
        }
    }

    public static string GetProfileFileLocation(Game game, string id) =>
        Path.Combine(CogworkPaths.GetProfilesSubDirectoryNoCreate(game, id), "profile.json");

    public void SaveLockFile()
    {
        ModListLockFile lockFile = new(
            Added.Select(x => new KeyValuePair<string, PackageVersionNumber>(
                x.Key.ToStringSimpleWithSource(),
                x.Value.Version
            )),
            Dependencies.Select(x => new KeyValuePair<string, PackageVersionNumber>(
                x.Key.ToStringSimpleWithSource(),
                x.Value.Version
            ))
        );

        lockFile.Save(_lazy.ProfilePackageLockPath);
    }

    public bool Add(IEnumerable<Package> packages) => Add(packages.Select(x => x.Latest));

    public bool Add(IEnumerable<PackageVersion> packages)
    {
        bool updated = false;
        foreach (var package in packages)
        {
            updated |= Added.AddOrUpdateToHigherVersion(package);
        }
        DirtyRebuildDependencies();
        return updated;
    }

    public bool Add(Package package) => Add(package.Latest);

    public bool Add(PackageVersion package)
    {
        var updated = Added.AddOrUpdateToHigherVersion(package);
        DirtyRebuildDependencies();
        return updated;
    }

    public void Remove(IEnumerable<Package> packages)
    {
        foreach (var package in packages)
        {
            if (Added.Remove(package, out var packageVersion))
            {
                // Add this version temporarily to deps so version can't get downgraded on rebuild
                Dependencies.Add(package, packageVersion);
            }
        }
        DirtyRebuildDependencies();
    }

    public void Remove(Package package)
    {
        if (Added.Remove(package, out var packageVersion))
        {
            Dependencies.Add(package, packageVersion);
        }
        DirtyRebuildDependencies();
    }

    void DirtyRebuildDependencies()
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
        _onResolved();

        _lazy.SaveData();
        SaveLockFile();
    }

    public void UpdatePackages()
    {
        foreach (var dependency in Dependencies)
        {
            Dependencies[dependency.Key] = dependency.Key.Latest;
        }
        Add(Added.Keys.Select(x => x.Latest));
    }

    public async Task<bool> InstallPackages(CancellationToken cancellationToken = default)
    {
        var installRules = _lazy.Game.InstallRules;
        foreach (var package in AllPackages)
        {
            var files = CogworkPaths.GetProfileFilesDirectory(_lazy);
            _ = installRules.InstallPackage(package.Value, files, cancellationToken);
        }
        return true;
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
