using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core.Sources;

[JsonConverter(typeof(PackageSourceIdConverter))]
public readonly record struct PackageSourceId(string Site, string GameSlug)
{
    public static PackageSourceId Parse(string sourceId)
    {
        var split = sourceId.Split('/');
        if (split.Length > 2)
            throw new ArgumentException(
                $"Argument '{sourceId}' must have at max one divider ('/')"
            );

        var site = split[0];
        var game = split.Length < 2 ? string.Empty : split[1];

        return new(site, game);
    }

    public bool TryGetGame([NotNullWhen(true)] out Game? game) =>
        Game.NameToGame.TryGetValue(GameSlug, out game);

    public bool TryResolve([NotNullWhen(true)] out PackageSource? source) =>
        PackageSourceIndex.TryParseSourceId(this, out source);

    public override string ToString() => GameSlug == string.Empty ? Site : $"{Site}/{GameSlug}";
}

public readonly record struct PackageMarkdown(
    [property: JsonPropertyName("markdown")] string Markdown
);

[JsonConverter(typeof(PackageSourceConverter))]
public abstract class PackageSource
{
    public sealed class PackageSourceCache : ISaveWithJson
    {
        public DateTime LastFetch { get; set; }
    }

    public PackageSource Service => this;
    protected List<Package> Packages { get; set; } = [];
    public abstract PackageSourceId Uri { get; }
    public abstract string Id { get; }

    internal ConcurrentDictionary<string, Package> nameToPackage = [];

    public async Task EnsurePackageIndexIsFetchedAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        _ = await FetchPackageIndexAsync(maybeRefetch: false, progressFactory, cancellationToken);
    }

    public async Task FetchPackageIndexLatestAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        _ = await FetchPackageIndexAsync(maybeRefetch: true, progressFactory, cancellationToken);
    }

    public abstract Task<bool> FetchPackageIndexAsync(
        bool maybeRefetch,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
    );

    public async Task<List<Package>> GetPackagesAsync(
        Func<PackageSource, ProgressContext>? progressFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        await EnsurePackageIndexIsFetchedAsync(progressFactory, cancellationToken);
        return Packages;
    }

    internal async Task<List<Package>?> ParsePackageIndexAsync(string packageIndexBasePath)
    {
        var packageIndexPath = Path.Combine(packageIndexBasePath, "index");

        if (!Directory.Exists(packageIndexPath))
        {
            Cog.Error($"Package index directory '{packageIndexPath}' must exist.");
            return default;
        }

        Cog.Debug($"Loading JSON index '{packageIndexPath}'...");

        List<Package> allPackages = [];

        var result = Parallel.ForEach(
            Directory
                .EnumerateFiles(packageIndexPath)
                .Where(x =>
                    !x.EndsWith(".next", StringComparison.Ordinal)
                    && !x.EndsWith(".next.download", StringComparison.Ordinal)
                ),
            (indexFile, state) =>
            {
                try
                {
                    using var fileStream = new FileStream(
                        indexFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    );
                    using GZipStream zipStream = new(fileStream, CompressionMode.Decompress);
                    if (!TryParsePackageIndexJson(indexFile, zipStream, out var packages1))
                    {
                        state.Break();
                        return;
                    }

                    lock (allPackages)
                    {
                        allPackages.AddRange(packages1);
                    }
                }
                catch (Exception ex)
                {
                    Cog.Error(indexFile + ": " + ex.ToString());
                    return;
                }
            }
        );

        if (!result.IsCompleted)
        {
            return default;
        }

        Cog.Debug($"Loaded JSON index");
        return allPackages;
    }

    internal bool TryParsePackageIndexJson(
        string fileName,
        Stream data,
        [NotNullWhen(true)] out List<Package>? packages
    )
    {
        try
        {
            packages = JsonSerializer.Deserialize(data, JsonGen.Default.ListPackage);
            if (packages is null)
            {
                Cog.Error($"Package index file '{fileName}' deserialization returned null");
                return false;
            }
            ProcessPackages(packages);
            return true;
        }
        catch (JsonException ex)
        {
            byte[] buffer = new byte[100];
            data.Position = 0;
            _ = data.Read(buffer);
            var beginning = Encoding.UTF8.GetString(buffer);
            Cog.Error(
                $"Error reading package index file '{fileName}' with contents beginning with: '{beginning}'\n"
                    + "And error: "
                    + ex.ToString()
            );
            packages = default;
            return false;
        }
    }

    protected void ProcessPackages(List<Package> packages)
    {
        foreach (var package in packages)
        {
            ProcessPackage(package);
        }
    }

    protected void ProcessPackage(Package package)
    {
        package.Source = this;
        nameToPackage[package.FullName] = package;
    }

    public bool TryImportHiddenUniquePackage(
        PackageVersion packageVersion,
        [NotNullWhen(true)] out Package? package
    )
    {
        if (packageVersion.Package is not null)
        {
            throw new ArgumentException(
                $"PackageVersion '{packageVersion}' must not belong to a Package."
            );
        }

        if (nameToPackage.ContainsKey(packageVersion.GetFullName()))
        {
            package = null;
            return false;
        }

        package = new Package(packageVersion.Author, packageVersion.Name, [packageVersion]);
        ProcessPackage(package);
        return true;
    }

    public override string ToString()
    {
        var url = Service.Uri;
        return url.ToString();
    }

    public bool IsPackageDownloaded(PackageVersionReference packageVersion) =>
        IsPackageDownloaded(packageVersion, out _, out _, out _);

    public abstract bool IsPackageDownloaded(
        PackageVersionReference packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    );

    public abstract Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    );

    public virtual async Task<string?> ExtractAsync(
        PackageVersionReference packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !IsPackageDownloaded(
                packageVersion,
                out var zipPath,
                out var directoryPath,
                out var zipExists
            )
        )
        {
            Cog.Error($"Cannot extract package which is not downloaded: '{packageVersion}'");
            return null;
        }

        if (zipExists is false)
        {
            // already extracted
            return directoryPath;
        }

        var tempDirPath = directoryPath + ".temp";

        if (Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);

        if (Directory.Exists(tempDirPath))
            Directory.Delete(tempDirPath, recursive: true);
        {
            using FileStream fileStream = File.Open(zipPath, FileMode.Open);
            await ZipFile.ExtractToDirectoryAsync(fileStream, directoryPath, cancellationToken);
        }

        File.Delete(zipPath);
        return directoryPath;
    }

    public abstract Task<string> GetReadmeAsync(
        PackageVersionReference packageVersion,
        CancellationToken cancellationToken = default
    );
}
