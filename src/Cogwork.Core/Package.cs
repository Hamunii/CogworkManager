using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ZLinq;

namespace Cogwork.Core;

public readonly record struct VisualPackageVersion
{
    public string FullName { get; }
    public PackageVersionNumber Version { get; }
    public string? Source { get; }

    public VisualPackageVersion(KeyValuePair<string, PackageVersionNumber> keyValuePair)
        : this(keyValuePair.Key, keyValuePair.Value) { }

    public VisualPackageVersion(string packageId, PackageVersionNumber version)
    {
        Version = version;
        var split = packageId.AsSpan().Split('-');

        split.MoveNext();
        split.MoveNext();
        FullName = packageId[..split.Current.End];

        if (split.MoveNext())
        {
            Source = packageId[split.Current.Start..];
        }
    }

    public override string ToString() =>
        Source is { } ? $"{FullName}-{Version}-{Source}" : $"{FullName}-{Version}";
}

[JsonConverter(typeof(VersionRangeConverter))]
public readonly record struct VersionRange
{
    [Flags]
    public enum Kind
    {
        Default = 0,
        MinExclusive = 1 << 1,
        MaxExclusive = 1 << 2,
    }

    public PackageVersionNumber MinVersion { get; }
    public PackageVersionNumber MaxVersion { get; }
    public Kind RangeKind { get; }

    public VersionRange(
        PackageVersionNumber minVersion,
        PackageVersionNumber maxVersion,
        Kind rangeKind
    )
    {
        RangeKind = rangeKind;
        MinVersion = minVersion;
        MaxVersion = maxVersion;

        if ((rangeKind & Kind.MinExclusive) is not Kind.Default)
            MinVersion = MinVersion.GetClosestHigherVersion();

        if ((rangeKind & Kind.MaxExclusive) is not Kind.Default)
            MaxVersion = MaxVersion.GetClosestLesserVersion();

        Cog.Warning($"New VersionRange: {this}");
    }

    public readonly bool IsInRange(PackageVersionNumber versionNumber) =>
        versionNumber.IsHigherThanOrEqual(MinVersion)
        && versionNumber.IsLessThanOrEqual(MaxVersion);

    // https://learn.microsoft.com/en-us/nuget/concepts/package-versioning?tabs=semver20sort#version-ranges
    public static VersionRange ParseRange(scoped ReadOnlySpan<char> rangeSyntax)
    {
        var split = rangeSyntax.Split(',');

        split.MoveNext();
        var splitRange = rangeSyntax[split.Current];
        bool isMinExclusive;
        switch (splitRange[0])
        {
            case '[':
                isMinExclusive = false;
                break;
            case '(':
                isMinExclusive = true;
                break;
            default:
                return new WildcardVersion(splitRange).ToVersionRange();
        }

        WildcardVersion wildRangeMin = new(splitRange[1..]);

        if (!split.MoveNext())
        {
            throw new InvalidDataException("Incomplete version range syntax.");
        }

        splitRange = rangeSyntax[split.Current];
        var isMaxExclusive = splitRange[^1] switch
        {
            ']' => false,
            ')' => true,
            _ => throw new InvalidDataException(
                $"Missing closing bracket in version range syntax: '{rangeSyntax}'"
            ),
        };

        WildcardVersion wildRangeMax = new(splitRange[..^1]);

        var rangeMin = wildRangeMin.ToVersionRange().MinVersion;
        var rangeMax = wildRangeMax.ToVersionRange().MaxVersion;

        var rangeKind = Kind.Default;

        if (isMinExclusive)
            rangeKind |= Kind.MinExclusive;

        if (isMaxExclusive)
            rangeKind |= Kind.MaxExclusive;

        VersionRange range = new(rangeMin, rangeMax, rangeKind);
        return range;
    }

    public readonly bool TryResolveVersion(
        Package package,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        var versionRange = this;

        packageVersion = package
            .Versions.AsValueEnumerable()
            .FirstOrDefault(x => versionRange.IsInRange(x.Version));

        return packageVersion is { };
    }

    public override string ToString()
    {
        StringBuilder sb = new();

        if ((RangeKind & Kind.MinExclusive) is not Kind.Default)
        {
            sb.Append('(');
            var escapedMinVersion = MinVersion.GetClosestLesserVersion();
            sb.Append(escapedMinVersion.ToStringWithWildcards());
        }
        else
        {
            sb.Append('[');
            sb.Append(MinVersion.ToStringWithWildcards());
        }

        sb.Append(',');

        if ((RangeKind & Kind.MaxExclusive) is not Kind.Default)
        {
            var escapedMaxVersion = MaxVersion.GetClosestHigherVersion();
            sb.Append(escapedMaxVersion.ToStringWithWildcards());
            sb.Append(')');
        }
        else
        {
            sb.Append(MaxVersion.ToStringWithWildcards());
            sb.Append(']');
        }

        return sb.ToString();
    }
}

public readonly record struct WildcardVersion
{
    public readonly record struct NumberOrWildcard(long? Number)
    {
        public static NumberOrWildcard Wildcard() => new();

        [MemberNotNullWhen(false, nameof(Number))]
        public bool IsWildcard() => Number is null;

        public Range Expand() => Number is { } value ? new(value) : Range.Full();
    }

    public readonly record struct Range(long Min, long Max)
    {
        public Range(long value)
            : this(value, value) { }

        public static Range Full() => new(0, long.MaxValue);
    }

    public NumberOrWildcard Major { get; }
    public NumberOrWildcard Minor { get; }
    public NumberOrWildcard Patch { get; }

    public WildcardVersion(NumberOrWildcard major, NumberOrWildcard minor, NumberOrWildcard patch)
    {
        Major = major;
        if (major.IsWildcard())
        {
            Minor = NumberOrWildcard.Wildcard();
            Patch = NumberOrWildcard.Wildcard();
            return;
        }

        Minor = minor;
        if (minor.IsWildcard())
        {
            Patch = NumberOrWildcard.Wildcard();
            return;
        }

        Patch = patch;
    }

    public WildcardVersion(scoped ReadOnlySpan<char> version)
    {
        var split = version.Split('.');

        if (
            split.MoveNext()
            && long.TryParse(version[split.Current], CultureInfo.InvariantCulture, out var major)
        )
            Major = new(major);
        else
        {
            Major = NumberOrWildcard.Wildcard();
            Minor = NumberOrWildcard.Wildcard();
            Patch = NumberOrWildcard.Wildcard();
            return;
        }

        if (
            split.MoveNext()
            && long.TryParse(version[split.Current], CultureInfo.InvariantCulture, out var minor)
        )
            Minor = new(minor);
        else
        {
            Minor = NumberOrWildcard.Wildcard();
            Patch = NumberOrWildcard.Wildcard();
            return;
        }

        if (
            split.MoveNext()
            && long.TryParse(version[split.Current], CultureInfo.InvariantCulture, out var patch)
        )
            Patch = new(patch);
        else
        {
            Patch = NumberOrWildcard.Wildcard();
            return;
        }
    }

    public VersionRange ToVersionRange()
    {
        var major = Major.Expand();
        var minor = Minor.Expand();
        var patch = Patch.Expand();
        var min = new PackageVersionNumber(major.Min, minor.Min, patch.Min);
        var max = new PackageVersionNumber(major.Max, minor.Max, patch.Max);
        return new(min, max, VersionRange.Kind.Default);
    }
}

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

    public static bool TryGetPackageWithNoVersion(
        PackageSourceIndex sourceIndex,
        ReadOnlySpan<char> fullName,
        [NotNullWhen(true)] out Package? package
    ) => TryGetPackage(sourceIndex, fullName, out package, hasVersion: false, out _, out _);

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
        packageVersion = Versions.FirstOrDefault(x => version == x.Version);
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
                        if (source is null)
                        {
                            Cog.Debug(
                                $"Package for '{fullNameWithVersion}' was not found in any sources: "
                                    + string.Join(
                                        ", ",
                                        Package.Source.SourceIndex.Sources.Select(x => x.ToString())
                                    )
                            );
                        }
                        else
                        {
                            Cog.Debug(
                                $"Package for '{fullNameWithVersion}' in '{source}' was not found."
                            );
                        }
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

    public Task<string?> ExtractAsync(CancellationToken cancellationToken = default) =>
        Package.Source.Service.ExtractAsync(this, cancellationToken);

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
[JsonConverter(typeof(PackageVersionNumberConverter))]
public readonly record struct PackageVersionNumber
{
    public long Major { get; init; }
    public long Minor { get; init; }
    public long Patch { get; init; }

    public PackageVersionNumber(long major, long minor, long patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public PackageVersionNumber(scoped ReadOnlySpan<char> version)
    {
        var split = version.Split('.');
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

    public bool IsHigherThanOrEqual(PackageVersionNumber other)
    {
        if (IsHigherThan(other))
            return true;

        if (Patch >= other.Patch)
            return true;

        return false;
    }

    public bool IsLessThanOrEqual(PackageVersionNumber other) => !IsHigherThan(other);

    public PackageVersionNumber GetClosestLesserVersion()
    {
        if (Patch > 1)
            return this with { Patch = Patch - 1 };

        if (Minor > 1)
            return this with { Minor = Minor - 1, Patch = long.MaxValue };

        if (Major > 1)
            return this with { Major = Major - 1, Minor = long.MaxValue, Patch = long.MaxValue };

        throw new InvalidDataException($"No valid lesser version for: {this}");
    }

    public PackageVersionNumber GetClosestHigherVersion()
    {
        if (Patch < long.MaxValue)
            return this with { Patch = Patch + 1 };

        if (Minor < long.MaxValue)
            return this with { Minor = Minor + 1, Patch = 0 };

        if (Major < long.MaxValue)
            return this with { Major = Major + 1, Minor = 0, Patch = 0 };

        throw new InvalidDataException($"No valid higher version for: {this}");
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public string ToStringWithWildcards() =>
        Patch is not long.MaxValue ? $"{Major}.{Minor}.{Patch}"
        : Minor is not long.MaxValue ? $"{Major}.{Minor}.*"
        : Major is not long.MaxValue ? $"{Major}.*"
        : "*";
}
