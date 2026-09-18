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
public readonly record struct UserSource(bool Visible, PackageSource Source);

public sealed class PackageSourceIndex
{
    static readonly Dictionary<PackageSourceId, PackageSource> s_SourceCache = [];

    [JsonIgnore]
    public ReadOnlyCollection<UserSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<UserSource> PackageSources { get; } = [];

    readonly Dictionary<string, Package> dominantPackages = [];

    public PackageSourceIndex() { }

    public PackageSourceIndex(PackageSource packageSource)
    {
        Add(packageSource);
    }

    public PackageSourceIndex(IEnumerable<PackageSourceId> uris) => Import(uris);

    public Package GetOrMakeDominantPackage(Package package)
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dominantPackages,
            package.FullName,
            out var exists
        );

        if (exists)
        {
            var previousSource = value!.Source;

            if (previousSource == package.Source)
                return value;

            var newIndex = PackageSources.FindIndex(x => x.Source == package.Source);
            var oldIndex = PackageSources.FindIndex(x => x.Source == previousSource);

            if (!PackageSources[newIndex].Visible && PackageSources[oldIndex].Visible)
                return value;

            if (oldIndex < newIndex)
                return value;

            // TODO: Dominant package resolution strategies per UserSource, such as:
            //
            // 1. highest available version wins if allowed (so not against 2.)
            //   - tie is solved by index
            // 2. smaller index always wins
            // 3. smaller index always wins if package has been referenced (current hardcoded solution)
            //   - package is only referenced if it's a added or a transitive dependency
            //
            // Notes:
            // - Visible is always preferred over not Visible
            // - Explicitly added packages should ALWAYS take priority
        }
        return value = package;
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
            if (!TryImportFromUri(uri, out _))
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

        Add(source);
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

    public void AddHidden(PackageSource packageSource)
    {
        if (PackageSources.Any(x => x.Source == packageSource))
            return;

        GetOrCreateSource(packageSource);
        PackageSources.Add(new(Visible: false, packageSource));
    }

    public void Add(PackageSource packageSource)
    {
        GetOrCreateSource(packageSource);
        Remove(packageSource);
        PackageSources.Add(new(Visible: true, packageSource));
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
