using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core;

public sealed class PackageSourceIndex
{
    static readonly Dictionary<PackageSourceId, PackageSource> s_SourceCache = [];

    [JsonIgnore]
    public ReadOnlyCollection<PackageSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<PackageSource> PackageSources { get; } = [];

    readonly Dictionary<string, Package> dominantPackages = [];

    public PackageSourceIndex() { }

    public PackageSourceIndex(PackageSource packageSource)
    {
        AddIfNotExists(packageSource);
    }

    public PackageSourceIndex(IEnumerable<PackageSourceId> uris) => Import(uris);

    public void MakePackageDominant(Package package)
    {
        dominantPackages[package.FullName] = package;
    }

    public Package GetDominantPackage(Package package)
    {
        if (dominantPackages.TryGetValue(package.FullName, out var dominant))
        {
            return dominant;
        }

        // One package must always be dominant to avoid cases where
        // a package is installed from multiple sources at once.
        // We don't necessarily care about the logic for which package
        // is dominant if it's not defined by the user. If the user cares,
        // they must explicitly add a package to make it dominant.
        MakePackageDominant(package);
        return package;
    }

    public PackageVersion GetDominantPackage(PackageVersion packageVersion)
    {
        var package = packageVersion.Package;
        var dominant = GetDominantPackage(package);

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
            if (!TryImportFromUri(uri, out var packageSource))
            {
                Cog.Warning($"Could not parse package source uri: '{uri}'");
            }
            // if (!PackageSources.Any(x => uri == x.Service.Uri)) { }
        }
    }

    public bool TryImportFromUri(
        PackageSourceId uri,
        [NotNullWhen(true)] out PackageSource? source
    ) => TryParseSourceIdAndImportIfIndexIsNotNull(uri, out source, this);

    // This is a horrible method.
    public static bool TryParseSourceIdAndImportIfIndexIsNotNull(
        PackageSourceId uri,
        [NotNullWhen(true)] out PackageSource? source,
        PackageSourceIndex? index = null
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
                if (index is null)
                {
                    source = new LocalPackageSource();
                    return true;
                }

                source = index.AddIfNotExists(uri, () => new LocalPackageSource());
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

                if (index is null)
                {
                    source = new ThunderstoreCommunity(uri.GameSlug);
                    return true;
                }

                source = index.AddIfNotExists(
                    uri,
                    () => new ThunderstoreCommunity(uri.GameSlug)
                );
                return true;
        }

        return false;
    }

    public PackageSource AddIfNotExists(PackageSource packageSource) =>
        AddIfNotExists(packageSource.Uri, () => packageSource);

    public PackageSource AddIfNotExists(PackageSourceId id, Func<PackageSource> getPackageSource)
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            s_SourceCache,
            id,
            out var exists
        );

        if (exists)
        {
            if (!PackageSources.Contains(value!))
            {
                PackageSources.Add(value!);
                return value!;
            }

            Cog.Debug($"Package source already exists {id} {new StackTrace(true)}");
            return value!;
        }

        value = getPackageSource();
        PackageSources.Add(value);
        return value;
    }

    public void Add(PackageSource packageSource)
    {
        PackageSources.Add(packageSource);
    }

    public async Task<IEnumerable<Package>> GetAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        Cog.Information($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.GetPackagesAsync(progressFactory, cancellationToken))
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
            .Select(x => x.FetchPackageIndexAutomaticAsync(progressFactory, cancellationToken))
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
            .Select(x => x.FetchPackageIndexManualAsync(progressFactory, cancellationToken))
            .ToArray();

        await Task.WhenAll(fetchTasks);
    }
}
