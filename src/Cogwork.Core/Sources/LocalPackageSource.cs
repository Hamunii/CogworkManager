using System.Data;
using System.IO.Compression;
using System.Text.Json;
using Cogwork.Core.Extensions;
using ZLinq;

namespace Cogwork.Core.Sources;

public sealed class LocalPackageSource : PackageSource
{
    public static LocalPackageSource Instance { get; } = new();

    public override PackageSourceId Uri { get; } = new("local", string.Empty);

    public override string Id => field ??= Uri.ToString();

    public string PackageIndexPath { get; } =
        Path.Combine(CogworkPaths.GetPackagesSubDirectory("local"), "local-index.json");

    DateTime _lastFetch;

    public override bool IsPackageDownloaded(
        PackageVersionReference packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        // For local packages, it might be best if only one version is allowed?
        // This might prevent accidentally using outdated versions, which might
        // also not even match the actual packages uploaded to e.g. Thunderstore.
        // var version = packageVersion.Version.ToString();
        var version = "latest";

        return IsPackageDownloaded(
                packageVersion,
                withVersionName: version,
                out zipFileLocation,
                out directoryPath,
                out zipExists
            )
            ||
            // I don't feel good about this because we are passing the "latest" package's
            // paths, but the API promises that one of those paths is true if this returns true,
            // but if we return true here and the previous didn't, this implementation doesn't
            // conform to the API.
            IsPackageDownloaded(packageVersion, withVersionName: "next", out _, out _, out _);
    }

    public static bool IsPackageDownloaded(
        PackageVersionReference packageVersion,
        string withVersionName,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        var version = withVersionName;

        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            "local",
            packageVersion.FullName
        );

        zipFileLocation = Path.Combine(installPathRoot, $"{version}.zip");
        directoryPath = Path.Combine(installPathRoot, version, "files");

        var dirExists = Directory.Exists(directoryPath);
        zipExists = File.Exists(zipFileLocation);
        return dirExists || zipExists;
    }

    public override Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        var PackageVersionReference = (PackageVersionReference)packageVersion;
        if (!IsPackageDownloaded(PackageVersionReference))
        {
            Cog.Error(
                $"Attempting to download local package '{packageVersion}' which is not found."
            );
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public override async Task<string> GetReadmeAsync(
        PackageVersionReference packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        var dir =
            await ExtractAsync(packageVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Local package should always be installed");

        return await File.ReadAllTextAsync(Path.Combine(dir, "README.md"), cancellationToken);
    }

    internal static bool NeedsReinstall(PackageVersionReference packageVersion) =>
        IsPackageDownloaded(
            packageVersion,
            withVersionName: "next",
            out var _,
            out var _,
            out var _
        );

    public override async Task<string?> ExtractAsync(
        PackageVersionReference packageVersion,
        CancellationToken cancellationToken = default
    )
    {
        if (
            IsPackageDownloaded(
                packageVersion,
                withVersionName: "next",
                out var zipPathTemp,
                out var directoryPathTemp,
                out var zipExistsTemp
            )
        )
        {
            _ = IsPackageDownloaded(
                packageVersion,
                withVersionName: "latest",
                out var zipPathFinal,
                out var directoryPathFinal,
                out var zipExistsFinal
            );

            if (Directory.Exists(directoryPathFinal))
            {
                Directory.Delete(directoryPathFinal, recursive: true);
            }

            if (Directory.Exists(directoryPathTemp))
            {
                Directory.Move(directoryPathTemp, directoryPathFinal);
            }

            if (zipExistsTemp)
            {
                if (zipExistsFinal)
                    File.Delete(zipPathFinal);

                File.Move(zipPathTemp, zipPathFinal);
            }
        }
        return await base.ExtractAsync(packageVersion, cancellationToken);
    }

    public override async Task<bool> FetchPackageIndexAsync(
        bool maybeRefetch,
        Func<PackageSource, ProgressContext>? progressFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (_lastFetch != default)
        {
            // Ignore not fetching because it's actually kind of important that the
            // local package source is always up to date.
            // if (!maybeRefetch)
            //     return true;

            if (_lastFetch > DateTime.Now - TimeSpan.FromSeconds(2))
            {
                Cog.Debug("Local package index is already fetched");
                return true;
            }
        }

        if (!File.Exists(PackageIndexPath))
        {
            Cog.Debug("Fetched local package index (which has not been created yet)");
            return true;
        }

        using var fileStream = File.Open(
            PackageIndexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        var packages = JsonSerializer.Deserialize(fileStream, JsonGen.Default.ListPackage);
        if (packages is null)
        {
            Packages ??= [];
            Cog.Error($"Package index file '{PackageIndexPath}' deserialization returned null");
            return false;
        }
        ProcessPackages(packages);
        Packages = packages;

        _lastFetch = DateTime.Now;
        Cog.Debug("Fetched local package index");
        return true;
    }

    public string? ImportPackage(string path)
    {
        Cog.Information($"Importing local package at '{path}'");

        var manifest = Path.Combine(path, "manifest.json");

        if (File.Exists(path))
        {
            using FileStream fileStream = File.Open(path, FileMode.Open);
            var archive = new ZipArchive(fileStream);

            var manifestFile = archive.Entries.FirstOrDefault(x =>
                x.FullName.Equals("manifest.json", StringComparison.Ordinal)
            );
            if (manifestFile is null)
            {
                return "Manifest file not found in archive";
            }

            using var manifestStream = manifestFile.Open();
            return ImportPackageFromManifest(
                path,
                manifest,
                manifestStream,
                () =>
                {
                    manifestStream.Dispose();
                    archive.Dispose();
                    fileStream.Dispose();
                }
            );
        }
        else if (!File.Exists(manifest))
        {
            throw new FileNotFoundException($"Manifest not found: '{manifest}'");
        }
        else
        {
            using var manifestStream = File.OpenRead(manifest);
            return ImportPackageFromManifest(path, manifest, manifestStream);
        }
    }

    private string? ImportPackageFromManifest(
        string path,
        string manifestPath,
        Stream manifestStream,
        Action? disposeStreams = null
    )
    {
        var packageVersion = JsonSerializer.Deserialize(
            manifestStream,
            JsonGen.Default.PackageVersion
        );

        disposeStreams?.Invoke();

        if (packageVersion is null)
        {
            return $"Package manifest file '{manifestPath}' deserialization returned null";
        }

        _ = FetchPackageIndexAsync(maybeRefetch: true, default).Result;

        packageVersion = packageVersion.WithVersion(
            packageVersion.Version.WithMetadata($"id.{Guid.NewGuid():N}")
        );

        var package = new Package(packageVersion.Author, packageVersion.Name, [packageVersion]);

        // Overwrites existing package
        ProcessPackage(package);

        if (
            IsPackageDownloaded(
                (PackageVersionReference)packageVersion,
                withVersionName: "next",
                out var zipFileLocation,
                out var directoryPath,
                out var zipExists
            )
        )
        {
            if (zipExists)
            {
                File.Delete(zipFileLocation);
            }

            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }

        if (File.Exists(path))
        {
            File.Copy(path, zipFileLocation);
        }
        else
        {
            Utils.CopyDirectory(path, directoryPath, recursive: true);
        }

        Packages = [.. nameToPackage.Select(x => x.Value)];
        var localPackageIndex = JsonSerializer.Serialize(Packages, JsonGen.Default.ListPackage);
        File.WriteAllText(PackageIndexPath, localPackageIndex);

        Cog.Information($"Imported package '{packageVersion}'");

        _lastFetch = DateTime.Now;
        return null;
    }
}
