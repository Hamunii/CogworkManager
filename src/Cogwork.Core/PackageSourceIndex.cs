using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core;

/// <param name="Visible">Visibility to package fetching methods.</param>
public readonly record struct UserSource(
    PackageSource Source,
    SourceDominanceStrategy DominanceStrategy,
    SourceDominanceEntry DominanceEntry
)
{
    public bool Visible => DominanceEntry != SourceDominanceEntry.Hidden;
}

/// <summary>
/// Defines the dominance strategy for dependency resolution.
/// </summary>
public enum SourceDominanceStrategy
{
    ByPriority,
    ByHighestAvailableVersion,
}

/// <summary>
/// Defines when the source should enter dominance resolution for dependency resolution.
/// </summary>
public enum SourceDominanceEntry
{
    Always,
    IfPackageReferenced,

    /// <summary>
    /// Only for package sources which shouldn't show up in searches.
    /// </summary>
    Hidden,
}

public sealed class PackageSourceIndex
{
    static readonly Dictionary<PackageSourceId, PackageSource> s_SourceCache = [];

    [JsonIgnore]
    public ReadOnlyCollection<UserSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<UserSource> PackageSources { get; } = [];

    readonly Dictionary<string, PackageReference> dominantPackages = [];
    readonly Dictionary<PackageSource, UserSource> sourceToUser = [];

    ModList modList = null!;

    public PackageSourceIndex() { }

    public PackageSourceIndex(UserSource userSource)
    {
        AddOrUpdate(userSource);
    }

    public PackageSourceIndex(IEnumerable<PackageSourceId> uris) => Import(uris);

    public UserSource GetAsUserSource(PackageSource packageSource)
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            sourceToUser,
            packageSource,
            out var exists
        );

        if (exists)
            return value;

        value = new(
            packageSource,
            SourceDominanceStrategy.ByHighestAvailableVersion,
            packageSource is LocalPackageSource // hardcoded for now with sensible values.
                ? SourceDominanceEntry.IfPackageReferenced
                : SourceDominanceEntry.Always
        );

        // Example:
        // 1. local { ByHighestAvailableVersion, IfPackageReferenced }
        // 2. thunderstore { ByHighestAvailableVersion, Always }
        // 3. hexium { ByHighestAvailableVersion, Always }

        Cog.Information(
            $"Source '{packageSource.Id}' does not have user config, set default:\n{value}"
        );
        return value;
    }

    public void SetModList(ModList modList) => this.modList = modList;

    public void ResetPackageDominance() => dominantPackages.Clear();

    public Package GetOrMakeDominantPackage(Package candidatePackage)
    {
        ref var refDominant = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dominantPackages,
            candidatePackage.FullName,
            out var exists
        );

        var candidate = (PackageReference)candidatePackage;

        if (!exists)
            return refDominant = candidate;

        // Dominance exists only for resolving dependency packages.
        // Therefore 'Added' packages must always be dominant.
        if (modList.Added.ContainsKey(refDominant))
            return refDominant;

        // Dependency dominance evaluation rules:
        //
        // 1. Explicitly Added packages ALWAYS win
        // 2. Sources with SourceDominanceStrategy.ByPriority win with highest priority.
        // 3. Sources with SourceDominanceStrategy.ByHighestAvailableVersion win if
        //      they have highest version. If equal, it wins by priority.
        // 4. Sources with SourceDominanceEntry.Always are ALWAYS evaluated for dominance.
        // 5. Sources SourceDominanceEntry.IfPackageReferenced is only evaluated for
        //      dominance if the package source is on the candidatePackage fed to this method.
        //      Therefore its evaluation happens by concatenating it to packageFromAllContenders.

        var packageFromAllContenders = Sources
            .Where(x =>
                x.Source != candidate.Source && x.DominanceEntry == SourceDominanceEntry.Always
            )
            .Select(source =>
            {
                _ = Package.TryGetPackage(source.Source, candidate, out var package);
                return package!;
            })
            .Where(x => x is { })
            .Concat([candidate]);

        foreach (var contenderPackage in packageFromAllContenders)
        {
            var contender = (PackageReference)contenderPackage;
            var previousSource = refDominant.Source;

            if (previousSource == contender.Source)
                continue;

            if (modList.Added.ContainsKey(contender))
                return refDominant = contender;

            var oldIndex = PackageSources.FindIndex(x => x.Source == previousSource);
            var newIndex = PackageSources.FindIndex(x => x.Source == contender.Source);

            Debug.Assert(
                oldIndex != -1 || newIndex != -1,
                $"Packages given to this method should always be from this {nameof(PackageSourceIndex)}."
            );

            var oldSource = PackageSources[oldIndex];
            var newSource = PackageSources[newIndex];

            if (
                oldSource.DominanceEntry != SourceDominanceEntry.Hidden
                && newSource.DominanceEntry == SourceDominanceEntry.Hidden
            )
                continue;

            bool oldHasHigherPriority = oldIndex < newIndex;

            if (
                oldHasHigherPriority
                && oldSource.DominanceStrategy == SourceDominanceStrategy.ByPriority
            )
                continue;

            switch (newSource.DominanceStrategy)
            {
                case SourceDominanceStrategy.ByPriority:
                    if (oldHasHigherPriority)
                        continue;
                    break;
                case SourceDominanceStrategy.ByHighestAvailableVersion:
                    var newHighestVersion = contenderPackage.Versions.First().Version;
                    var oldHighestVersion = refDominant.Resolve().Versions.First().Version;

                    if (oldHighestVersion.IsHigherThan(newHighestVersion))
                        continue;

                    if (
                        oldHighestVersion.IsHigherThanOrEqual(newHighestVersion)
                        && oldHasHigherPriority
                    )
                        continue;

                    break;
            }

            refDominant = contender;
        }
        return refDominant;
    }

    public PackageVersion GetOrMakeDominantPackage(PackageVersion packageVersion)
    {
        var package = packageVersion.Package;
        var dominant = GetOrMakeDominantPackage(package);

        if (ReferenceEquals(dominant, package))
        {
            return packageVersion;
        }

        if (!dominant.TryGetVersion(packageVersion.Version, out var dominantVersion))
        {
            throw new UnreachableException("PackageVersion must have existed to get here");
        }

        return dominantVersion;
    }

    public void Import(IEnumerable<PackageSourceId> uris)
    {
        foreach (var uri in uris)
        {
            if (!TryImportFromUri(uri))
            {
                Cog.Warning($"Could not parse package source uri: '{uri}'");
            }
        }
    }

    public bool TryImportFromUri(PackageSourceId uri) => TryImportFromUri(uri, out _);

    public bool TryImportFromUri(PackageSourceId uri, [NotNullWhen(true)] out PackageSource? source)
    {
        if (!TryParseSourceId(uri, out source))
            return false;

        var userSource = GetAsUserSource(source);
        if (userSource.Source is LocalPackageSource)
            AddOrUpdate(userSource, atIndex: 0);
        else
            AddOrUpdate(userSource);
        return true;
    }

    public static bool TryParseSourceId(
        PackageSourceId uri,
        [NotNullWhen(true)] out PackageSource? source
    )
    {
        source = default;

        switch (uri.Site)
        {
            case "local":
                if (uri.GameSlug != string.Empty)
                {
                    throw new NotImplementedException("Local source can't specify game yet.");
                }

                source = GetOrCreateSource(uri, () => LocalPackageSource.Instance);
                return true;
            // case "test":
            //     source = new(new TestPackageSource());
            //     return true;
            case "thunderstore.io":
            case "thunderstore.dev":
                if (!uri.TryGetGame(out var game))
                {
                    Cog.Debug($"Couldn't find game by name '{uri.GameSlug}'");
                }

                source = GetOrCreateSource(uri, () => new ThunderstoreCommunity(uri));
                return true;
        }

        return false;
    }

    static PackageSource GetOrCreateSource(PackageSource source) =>
        GetOrCreateSource(source.Uri, () => source);

    static PackageSource GetOrCreateSource(PackageSourceId id, Func<PackageSource> getPackageSource)
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            s_SourceCache,
            id,
            out var exists
        );

        if (exists)
        {
            return value!;
        }

        value = getPackageSource();
        return value;
    }

    public void AddOrUpdate(UserSource userSource, int atIndex = -1)
    {
        GetOrCreateSource(userSource.Source);
        if (atIndex is -1)
        {
            atIndex = PackageSources.FindIndex(x => x.Source == userSource.Source);
        }
        Remove(userSource.Source);

        if (atIndex is not -1)
            PackageSources.Insert(atIndex, userSource);
        else
            PackageSources.Add(userSource);
    }

    public bool Remove(PackageSource packageSource) =>
        PackageSources.RemoveAll(x => x.Visible && x.Source == packageSource) != 0;

    public async Task<IEnumerable<Package>> GetAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        Cog.Information($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Where(x => x.Visible)
            .Select(x => x.Source.GetPackagesAsync(progressFactory, cancellationToken))
            .ToArray();

        await Task.WhenAll(fetchTasks);
        return fetchTasks.SelectMany(x => x.Result);
    }

    public async Task FetchAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Where(x => x.Visible)
            .Select(x =>
                x.Source.FetchPackageIndexAutomaticAsync(progressFactory, cancellationToken)
            )
            .ToArray();

        await Task.WhenAll(fetchTasks);
    }

    public async Task FetchAllPackagesManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Where(x => x.Visible)
            .Select(x => x.Source.FetchPackageIndexManualAsync(progressFactory, cancellationToken))
            .ToArray();

        await Task.WhenAll(fetchTasks);
    }
}
