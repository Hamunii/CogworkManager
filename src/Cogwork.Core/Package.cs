using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ZLinq;

namespace Cogwork.Core;

public sealed record Package
{
    public Author Author { get; }
    public string Name { get; }

    [JsonInclude]
    [JsonPropertyName("full_name")]
    public string FullName { get; }

    [JsonInclude]
    [JsonPropertyName("versions")]
    public PackageVersion[] Versions { get; internal set; }
    public PackageVersion Latest => Versions[0];
    public PackageSource Source { get; internal set; } = null!;

    [JsonConstructor]
    public Package(string fullName, PackageVersion[] versions)
    {
        FullName = fullName;
        Versions = versions;
        foreach (var version in versions)
        {
            version.Package = this;
        }

        var fullNameSpan = fullName.AsSpan();
        var enumerator = fullNameSpan.Split('-');
        enumerator.MoveNext();
        Author = fullNameSpan[enumerator.Current].ToString();
        enumerator.MoveNext();
        Name = fullNameSpan[enumerator.Current].ToString();
    }

    public static bool TryGetPackage(
        PackageSourceIndex sourceIndex,
        ReadOnlySpan<char> fullName,
        [NotNullWhen(true)] out Package? package,
        bool hasVersion,
        out PackageVersionNumber? version,
        [NotNullWhen(true)] out PackageSource? source
    )
    {
        var split = fullName.Split('-');
        split.MoveNext();
        split.MoveNext();
        var name = fullName[0..split.Current.End];

        if (hasVersion)
        {
            split.MoveNext();

            try
            {
                version = new(fullName[split.Current].ToString());
            }
            catch (Exception ex)
            {
                throw new ArgumentException(fullName.ToString(), ex);
            }
        }
        else
        {
            version = default;
        }

        source = default;
        package = default;

        if (!split.MoveNext())
        {
            foreach (var so in sourceIndex.Sources)
            {
                var dict = so.nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>();
                if (dict.TryGetValue(name, out package))
                {
                    source = so;
                    return true;
                }
            }
            return false;
        }

        var service = fullName[split.Current.Start..];
        if (service.Equals("ts", StringComparison.Ordinal))
        {
            source = sourceIndex.Thunderstore;
            if (source is null)
            {
                Cog.Warning($"No default package source available for '{fullName}'");
                return false;
            }
        }
        else if (TryGetPackageSource(sourceIndex, service, out var packageSource))
        {
            source = packageSource;
        }
        else
        {
            Cog.Warning($"No package source found for '{service}' ({fullName})");
            return false;
        }

        return source
            .nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>()
            .TryGetValue(name, out package);
    }

    static bool TryGetPackageSource(
        PackageSourceIndex sourceIndex,
        ReadOnlySpan<char> service,
        [NotNullWhen(true)] out PackageSource? packageSource
    )
    {
        foreach (var source in sourceIndex.Sources.AsValueEnumerable())
        {
            if (service.Equals(source.Service.Id, StringComparison.Ordinal))
            {
                packageSource = source;
                return true;
            }
        }

        packageSource = default;
        return false;
    }

    public static bool TryGetPackageVersion(
        PackageSourceIndex sourceIndex,
        ReadOnlySpan<char> fullNameWithVersion,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        if (
            !TryGetPackage(
                sourceIndex,
                fullNameWithVersion,
                out var package,
                hasVersion: true,
                out var version,
                out _
            )
        )
        {
            Cog.Warning(
                $"Package for '{fullNameWithVersion}' doesn't exist. "
                    + "Was the package source data fetched and imported first?"
                    + new StackTrace(true)
            );
            packageVersion = default;
            return false;
        }

        if (!package.TryGetVersion(version!.Value, out packageVersion))
        {
            return false;
        }

        return true;
    }

    public bool TryGetVersion(
        PackageVersionNumber version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => x.Version == version);
        if (packageVersion is null)
        {
            Cog.Debug($"Version '{version}' not found for '{ToStringSimpleWithSource()}'");
            packageVersion = Versions.FirstOrDefault();
            if (packageVersion is null)
            {
                Cog.Error($"No versions of '{ToStringSimpleWithSource()}' exist");
            }
        }
        return packageVersion is { };
    }

    public string ToStringSimpleWithSource()
    {
        var service = Source.Service;
        return $"{Author.Name}-{Name}-{service.Id}";
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Author.Name}-{Name} {{");

        foreach (var version in Versions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {version.Version}: {{");
            sb.AppendLine("    dependencies: [");
            foreach (var dependency in version.MarkedDependencies)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {dependency}");
            }
            sb.AppendLine("    ]");
            sb.AppendLine("  }");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}

public sealed record PackageVersion
{
    public PackageVersion[] MarkedDependencies =>
        field ??= [
            .. DependencyStrings
                .Select(fullNameWithVersion =>
                {
                    if (
                        !Package.TryGetPackage(
                            Package.Source.SourceIndex,
                            fullNameWithVersion,
                            out var package,
                            hasVersion: true,
                            out var version,
                            out var source
                        )
                    )
                    {
                        Cog.Debug(
                            $"Package for '{fullNameWithVersion}' in '{source}' was not found."
                        );
                        return null;
                    }

                    if (!package.TryGetVersion(version!.Value, out var packageVersion))
                    {
                        Cog.Error(
                            $"Package '{fullNameWithVersion}' exists but has no versions in '{source}'."
                        );
                        return null;
                    }

                    return packageVersion;
                })
                .Where(x => x is { })!,
        ];

    public PackageVersion[] AllDependencies
    {
        get
        {
            HashSet<PackageVersion> actualDependencies = [];
            CollectDependencies(actualDependencies);
            return field = [.. actualDependencies];
        }
    }

    public PackageVersionNumber Version { get; set; }

    [JsonInclude]
    [JsonPropertyName("version_number")]
    public string VersionString { get; }
    public Package Package { get; internal set; } = null!;

    [JsonInclude]
    [JsonPropertyName("dependencies")]
    public string[] DependencyStrings { get; }

    public PackageVersion(string versionString, string[] dependencyStrings)
    {
        VersionString = versionString;
        try
        {
            Version = new(versionString);
        }
        catch (Exception ex)
        {
            Cog.Error($"{versionString} :: {ex}");
        }
        DependencyStrings = dependencyStrings;
    }

    public bool IsDownloaded() => Package.Source.Service.IsPackageDownloaded(this);

    void CollectDependencies(HashSet<PackageVersion> actualDependencies)
    {
        foreach (var dependency in MarkedDependencies)
        {
            if (actualDependencies.Add(dependency))
            {
                dependency.CollectDependencies(actualDependencies);
            }
        }
    }

    public void CollectAllDependenciesToMap(Dictionary<Package, PackageVersion> map)
    {
        if (map.AddOrUpdateToHigherVersion(this))
        {
            CollectDependenciesToMapRecursive(map);
        }
    }

    void CollectDependenciesToMapRecursive(Dictionary<Package, PackageVersion> map)
    {
        foreach (var dependency in MarkedDependencies)
        {
            if (map.AddOrUpdateToHigherVersion(dependency))
            {
                dependency.CollectDependenciesToMapRecursive(map);
            }
        }
    }

    public void CollectAllDependenciesToDestination(
        Dictionary<Package, PackageVersion> map,
        Dictionary<Package, PackageVersion> destination
    )
    {
        var higher = map.GetHigherVersion(this);
        higher.CollectDependenciesToDestinationRecursive(map, destination);
    }

    void CollectDependenciesToDestinationRecursive(
        Dictionary<Package, PackageVersion> map,
        Dictionary<Package, PackageVersion> destination
    )
    {
        foreach (var dependency in MarkedDependencies)
        {
            var higher = map.GetHigherVersion(dependency);
            if (destination.TryAdd(higher.Package, higher))
            {
                higher.CollectDependenciesToDestinationRecursive(map, destination);
            }
        }
    }

    public override string ToString()
    {
        var service = Package.Source.Service;
        return $"{Package.Author.Name}-{Package.Name}-{Version}-{service.Id}";
    }
}

public readonly record struct Author(string Name)
{
    public static implicit operator string(Author author) => author.Name;

    public static implicit operator Author(string author) => new(author);
}

/// <summary>
/// A version representation with larger ints than <see cref="Version"/>
/// because mods can have version numbers so high that its int32 fields are not enough.
/// </summary>
public readonly record struct PackageVersionNumber
{
    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }
    readonly string _version;

    public PackageVersionNumber(string version)
        : this()
    {
        _version = version;
        var split = version.AsSpan().Split('.');
        split.MoveNext();
        Major = long.Parse(version[split.Current], CultureInfo.InvariantCulture);
        split.MoveNext();
        Minor = long.Parse(version[split.Current], CultureInfo.InvariantCulture);
        split.MoveNext();
        Patch = long.Parse(version[split.Current], CultureInfo.InvariantCulture);
    }

    public bool IsHigherThan(PackageVersionNumber other)
    {
        if (Major > other.Major)
            return true;

        if (Major < other.Major)
            return false;

        if (Minor > other.Minor)
            return true;

        if (Minor < other.Minor)
            return false;

        if (Patch > other.Patch)
            return true;

        return false;
    }

    public override string ToString() => _version;
}
