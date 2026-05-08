using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
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

    public PackageSourceIndex() { }

    public PackageSourceIndex(PackageSource packageSource)
    {
        Add(packageSource);
    }

    public PackageSourceIndex(IEnumerable<Uri> uris) => Import(uris);

    public void Import(IEnumerable<Uri> uris)
    {
        foreach (var uri in uris.AsValueEnumerable())
        {
            if (!PackageSources.Any(x => uri == x.Service.Uri))
            {
                if (TryParseFromUri(uri, out var packageSource))
                {
                    Add(packageSource);
                }
                else
                {
                    Cog.Warning($"Could not parse package source uri: '{uri}'");
                }
            }
        }
    }

    public static bool TryParseFromUri(Uri uri, [NotNullWhen(true)] out PackageSource? source)
    {
        source = default;

        switch (uri.Scheme)
        {
            case "cogman":
                if (uri.AbsolutePath == "sources/local")
                {
                    source = LocalPackageSource.Instance;
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
                    source = new ThunderstoreCommunity(game);
                    return true;
                }

                Cog.Warning($"Couldn't find game by name '{slug}'");
                break;
        }

        return false;
    }

    public void Add(PackageSource packageSource)
    {
        PackageSources.Add(packageSource);
        packageSource.SourceIndex = this;

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
