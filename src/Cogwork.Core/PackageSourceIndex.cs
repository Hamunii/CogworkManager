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
    /// <summary>
    /// The package source which is resolved when a package source is not defined.
    /// This should be Thunderstore, if Thunderstore is present.
    /// </summary>
    [JsonIgnore]
    public PackageSource? Thunderstore { get; private set; }

    [JsonIgnore]
    public ReadOnlyCollection<PackageSource> Sources => field ??= new(PackageSources);

    [JsonIgnore]
    List<PackageSource> PackageSources { get; } = [];

    readonly Dictionary<string, PackageSource> sourceCache = [];
    readonly Dictionary<string, Package> dominantPackages = [];

    public PackageSourceIndex() { }

    public PackageSourceIndex(PackageSource packageSource)
    {
        AddIfNotExists(packageSource);
    }

    public PackageSourceIndex(IEnumerable<Uri> uris) => Import(uris);

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

    public void Import(IEnumerable<Uri> uris)
    {
        foreach (var uri in uris.AsValueEnumerable())
        {
            if (!TryImportFromUri(uri, out var packageSource))
            {
                Cog.Warning($"Could not parse package source uri: '{uri}'");
            }
            // if (!PackageSources.Any(x => uri == x.Service.Uri)) { }
        }
    }

    public bool TryImportFromUri(Uri uri, [NotNullWhen(true)] out PackageSource? source) =>
        TryParseFromUri(uri, out source, this);

    public static bool TryParseFromUri(
        Uri uri,
        [NotNullWhen(true)] out PackageSource? source,
        PackageSourceIndex? index = null
    )
    {
        source = default;

        switch (uri.Scheme)
        {
            case "cogman":
                if (uri.AbsolutePath == "sources/local")
                {
                    if (index is null)
                    {
                        source = new LocalPackageSource();
                        return true;
                    }

                    ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
                        index.sourceCache,
                        $"cogman:sources/local",
                        out var exists
                    );
                    if (!exists)
                    {
                        value = new LocalPackageSource();
                        index.Add(value);
                    }

                    source = value!;
                    return true;
                }
                break;
            // case "test":
            //     source = new(new TestPackageSource());
            //     return true;
        }

        switch (uri.Authority)
        {
            case "thunderstore.io":
                var span = uri.AbsolutePath.AsSpan();
                var split = span.Split('/');

                split.MoveNext(); // skip /
                split.MoveNext(); // skip c
                split.MoveNext();

                var slug = span[split.Current];
                Cog.Verbose($"slug from uri: {slug} | {uri.AbsolutePath} | {uri}");

                var nameToGame = Game.NameToGame.GetAlternateLookup<ReadOnlySpan<char>>();
                if (nameToGame.TryGetValue(slug, out var game))
                {
                    if (index is null)
                    {
                        source = new ThunderstoreCommunity(game);
                        return true;
                    }

                    ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
                        index.sourceCache,
                        $"https://thunderstore.io/c/{game.Slug}/",
                        out var exists
                    );
                    if (!exists)
                    {
                        value = new ThunderstoreCommunity(game);
                        index.Add(value);
                    }

                    source = value!;
                    return true;
                }

                Cog.Warning($"Couldn't find game by name '{slug}'");
                break;
        }

        return false;
    }

    public void AddIfNotExists(PackageSource packageSource)
    {
        packageSource.SourceIndex = this;

        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            sourceCache,
            packageSource.Id,
            out var exists
        );

        if (exists)
        {
            Cog.Debug($"Package source already exists {packageSource.Id} {new StackTrace(true)}");
            return;
        }

        value = packageSource;
        PackageSources.Add(packageSource);

        if (Thunderstore is null && packageSource.Service is ThunderstoreCommunity)
        {
            Thunderstore = packageSource;
        }
    }

    public void Add(PackageSource packageSource)
    {
        packageSource.SourceIndex = this;
        PackageSources.Add(packageSource);

        if (Thunderstore is null && packageSource.Service is ThunderstoreCommunity)
        {
            Thunderstore = packageSource;
        }
    }

    public async Task<IEnumerable<Package>> GetAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Information($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources.Select(x => x.GetPackagesAsync(progressFactory)).ToArray();
#if DEBUG
        // Simulate at least some delay to make sure things are awaited properly.
        await Task.Delay(100);
#endif
        Task.WaitAll(fetchTasks);
        return fetchTasks.SelectMany(x => x.Result);
    }

    public async Task FetchAllPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.FetchPackageIndexAutomaticAsync(progressFactory))
            .ToArray();
#if DEBUG
        await Task.Delay(100);
#endif
        await Task.WhenAll(fetchTasks);
    }

    public async Task FetchAllPackagesManualAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null
    )
    {
        Cog.Debug($"Package sources count: {PackageSources.Count}");
        var fetchTasks = PackageSources
            .Select(x => x.FetchPackageIndexManualAsync(progressFactory))
            .ToArray();
#if DEBUG
        await Task.Delay(100);
#endif
        await Task.WhenAll(fetchTasks);
    }
}
