using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using Cogwork.Core.Extensions;

namespace Cogwork.Core;

internal class TestPackageSource : IPackageSourceService
{
    public string PackageIndexDirectory =>
        field ??= CogworkPaths.GetCacheIndexSubDirectory(Game.Slug, "test");

    public string PackageIndexCacheLocation =>
        field ??= Path.Combine(
            CogworkPaths.GetCacheIndexSubDirectory(Game.Slug),
            $"test-index-cache.json"
        );

    public string PackageInstallSubDirectory { get; } = "test";

    public Uri Uri => field ??= new($"test:0");
    public string Id => field ??= Uri.ToString();
    public Game Game => Game.Test;

    public bool IsIncompleteIndexCache() =>
        Directory
            .EnumerateFiles(PackageIndexDirectory)
            .Any(x => x.EndsWith(".todo", StringComparison.Ordinal));

    public string PackageIndexLocation(int index) =>
        Path.Combine(PackageIndexDirectory, $"{index}.json.zip");

    public async Task<bool> FetchIndexToCacheAsync(ProgressContext progress = default)
    {
        Cog.Information("[Test] Fetching: " + Uri);

        var totalBytes = 1024 * 1024;
        await SimulateDownloadAsync(totalBytes, progress);

        using var fileStream = new FileStream(
            PackageIndexLocation(0),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );

        using GZipStream gZip = new(fileStream, CompressionMode.Compress);
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonTestData));
        await memoryStream.CopyToAsync(gZip);

        Cog.Information("[Test] Fetched successfully.");
        return true;
    }

    public bool IsPackageDownloaded(PackageVersion packageVersion) =>
        IsPackageDownloaded(packageVersion, out _, out _, out _);

    public bool IsPackageDownloaded(
        PackageVersion packageVersion,
        out string zipFileLocation,
        out string directoryPath,
        out bool zipExists
    )
    {
        var package = packageVersion.Package;
        var version = packageVersion.Version.ToString();
        var installPathRoot = CogworkPaths.GetPackagesSubDirectory(
            PackageInstallSubDirectory,
            package.FullName
        );

        zipFileLocation = Path.Combine(installPathRoot, $"{version}.zip");
        directoryPath = Path.Combine(installPathRoot, version, "files");

        var dirExists = Directory.Exists(directoryPath);
        zipExists = File.Exists(zipFileLocation);
        return dirExists || zipExists;
    }

    public async Task<bool> DownloadPackageAsync(
        PackageVersion packageVersion,
        ProgressContext progress = default,
        CancellationToken cancellationToken = default
    )
    {
        if (IsPackageDownloaded(packageVersion, out var zipFileLocation, out _, out _))
        {
            Cog.Debug($"[Test] Package is already downloaded for '{packageVersion}'");
            return true;
        }

        var random = new Random(packageVersion.ToString().GetHashCode());
        var totalBytes = random.NextInt64(10_000, 100_000_000);
        await SimulateDownloadAsync(totalBytes, progress, cancellationToken);

        using var fileStream = new FileStream(
            zipFileLocation,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );

        using ZipArchive zip = new(fileStream, ZipArchiveMode.Create);
        {
            var entry = zip.CreateEntry("file.txt");
            using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
            writer.Write(packageVersion.ToString());
        }

        Cog.Debug($"[Test] Download complete for: {packageVersion}");
        return true;
    }

    private static async Task SimulateDownloadAsync(
        long totalBytes,
        ProgressContext progress,
        CancellationToken cancellationToken = default
    )
    {
        progress.OnContentLengthKnown?.Invoke(progress.Progress!, totalBytes);

        long readBytes = 0;
        while (readBytes < totalBytes)
        {
            await Task.Delay(16, cancellationToken);

            readBytes += 100_000;

            if (readBytes > totalBytes)
                readBytes = totalBytes;

            progress.Progress?.Report(readBytes);
        }
    }

    public async Task<string?> ExtractAsync(
        PackageVersion packageVersion,
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
        File.Delete(directoryPath);
        File.Delete(tempDirPath);

        {
            using FileStream fileStream = File.Open(zipPath, FileMode.Open);
            await ZipFile.ExtractToDirectoryAsync(fileStream, tempDirPath, cancellationToken);
        }

        Game.InstallRules.Map(packageVersion.Package, tempDirPath, directoryPath);

        File.Delete(zipPath);
        return directoryPath;
    }

    static readonly string jsonTestData = """
        [{
            "full_name": "MonoDetour-MonoDetour",
            "versions": [
                {
                    "description": "Easy and convenient .NET detouring library, powered by MonoMod.RuntimeDetour.",
                    "version_number": "0.7.9",
                    "dependencies": []
                }
            ]
        },
        {
            "full_name": "MonoDetour-MonoDetour_BepInEx_5",
            "versions": [
                {
                    "description": "HarmonyX interop & BepInEx 5 logger integration for MonoDetour. Initializes MonoDetour early as a side effect.",
                    "version_number": "0.7.9",
                    "dependencies": [
                        "BepInEx-BepInExPack-5.4.2100",
                        "MonoDetour-MonoDetour-0.7.9"
                    ]
                }
            ]
        },
        {
            "full_name": "Hamunii-AutoHookGenPatcher",
            "versions": [
                {
                    "description": "Automatically generates MonoMod.RuntimeDetour.HookGen's MMHOOK files during the BepInEx preloader phase.",
                    "version_number": "1.0.9",
                    "dependencies": [
                        "BepInEx-BepInExPack-5.4.2100",
                        "Hamunii-DetourContext_Dispose_Fix-1.0.5"
                    ]
                }
            ]
        },
        {
            "full_name": "PEAKModding-PEAKLib.Core",
            "versions": [
                {
                    "description": "PEAKLib.Core.",
                    "version_number": "1.0.1",
                    "dependencies": [
                        "MonoDetour-MonoDetour_BepInEx_5-0.7.9"
                    ]
                },
                {
                    "description": "PEAKLib.Core.",
                    "version_number": "1.0.0",
                    "dependencies": [
                        "Hamunii-AutoHookGenPatcher-1.0.9"
                    ]
                }
            ]
        },
        {
            "full_name": "PEAKModding-PEAKLib.Items",
            "versions": [
                {
                    "description": "PEAKLib.Items.",
                    "version_number": "1.0.1",
                    "dependencies": [
                        "PEAKModding-PEAKLib.Core-1.0.1"
                    ]
                },
                {
                    "description": "PEAKLib.Items.",
                    "version_number": "1.0.0",
                    "dependencies": [
                        "PEAKModding-PEAKLib.Core-1.0.0"
                    ]
                }
            ]
        }]
        """;
}
