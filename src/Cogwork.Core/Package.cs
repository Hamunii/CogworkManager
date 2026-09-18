using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ZLinq;

namespace Cogwork.Core;

[JsonConverter(typeof(VisualPackageVersionConverter))]
public readonly record struct VisualPackageVersion
{
    public Author Author { get; }
    public string Name { get; }
    public string FullName { get; }
    public PackageVersionNumber Version { get; }
    public PackageSourceId? Source { get; }

    public VisualPackageVersion(KeyValuePair<string, PackageVersionNumber> keyValuePair)
        : this(keyValuePair.Key, keyValuePair.Value) { }

    public VisualPackageVersion(string packageId)
    {
        var span = packageId.AsSpan();

        var everythingButSource = span.Split('/');
        everythingButSource.MoveNext();

        var left = span[everythingButSource.Current];
        var versionDivider = left.LastIndexOf('-');

        FullName = left[..versionDivider].ToString();
        var nameDivider = FullName.LastIndexOf('-');
        Author = FullName[..nameDivider].ToString();
        Name = FullName[(nameDivider + 1)..].ToString();

        Version = new(left[(versionDivider + 1)..]);

        if (!everythingButSource.MoveNext())
        {
            throw new InvalidDataException(
                "This constructor requires format 'author-name-version/uri'"
            );
        }

        var right = span[everythingButSource.Current.Start..];
        Source = PackageSourceId.Parse(right.ToString());
    }

    public VisualPackageVersion(string packageId, PackageVersionNumber version)
    {
        Version = version;

        var span = packageId.AsSpan();

        var everythingButSource = span.Split('/');
        everythingButSource.MoveNext();

        var left = span[everythingButSource.Current];
        var divider = left.LastIndexOf('-');

        FullName = left.ToString();
        Author = left[..divider].ToString();
        Name = left[(divider + 1)..].ToString();

        if (everythingButSource.MoveNext())
        {
            var right = span[everythingButSource.Current.Start..];
            Source = PackageSourceId.Parse(right.ToString());
        }
    }

    public VisualPackageVersion(
        Author author,
        string name,
        string fullName,
        PackageVersionNumber version,
        PackageSourceId? source
    )
    {
        Author = author;
        Name = name;
        FullName = fullName;
        Version = version;
        Source = source;
    }

    public Task<string?> ExtractAsync(
        PackageSource service,
        CancellationToken cancellationToken = default
    ) => service.ExtractAsync(this, cancellationToken);

    public Task<string?> ExtractAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } sourceId)
        {
            Cog.Warning($"Source was null for '{ToString()}'" + new StackTrace(true));
            return Task.FromResult<string?>(null);
        }

        if (!PackageSourceIndex.TryParseSourceId(sourceId, out var source))
        {
            Cog.Warning($"No package source found for '{sourceId}'" + new StackTrace(true));
            return Task.FromResult<string?>(null);
        }

        return source.ExtractAsync(this, cancellationToken);
    }

    public bool? IsDownloaded([NotNullWhen(true)] out string? directoryPath)
    {
        directoryPath = null;

        if (Source is not { } sourceId)
        {
            Cog.Warning($"Source was null for '{ToString()}'" + new StackTrace(true));
            return null;
        }

        if (!PackageSourceIndex.TryParseSourceId(sourceId, out var source))
        {
            Cog.Warning($"No package source found for '{sourceId}'" + new StackTrace(true));
            return null;
        }

        return source.IsPackageDownloaded(this, out _, out directoryPath, out _);
    }

    public override string ToString() =>
        Source is { } ? $"{FullName}-{Version}/{Source}" : $"{FullName}-{Version}";

    public string ToStringWithoutVersion() => Source is { } ? $"{FullName}/{Source}" : FullName;

    public static explicit operator VisualPackageVersion(PackageVersion packageVersion)
    {
        var package = packageVersion.Package;
        return new(
            package.Author,
            package.Name,
            package.FullName,
            packageVersion.Version,
            package.Source.Uri
        );
    }
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
        var min = new PackageVersionNumber(major.Min, minor.Min, patch.Min, null);
        var max = new PackageVersionNumber(major.Max, minor.Max, patch.Max, null);
        return new(min, max, VersionRange.Kind.Default);
    }
}

sealed partial class PackageList
{
    public List<Package> Values { get; set; } = [];
}

public readonly record struct PackageReference
{
    public readonly string FullName { get; }
    public readonly PackageSource Source { get; }

    public PackageReference(string fullName, PackageSource source)
    {
        FullName = fullName;
        Source = source;
    }

    public static PackageReference CreateOrThrow(string fullNameNoVersionWithSource)
    {
        if (TryCreateFrom(fullNameNoVersionWithSource, out var packageReference))
            return packageReference;

        throw new InvalidOperationException(
            $"Failed to create {nameof(PackageReference)} '{fullNameNoVersionWithSource}'"
        );
    }

    public static bool TryCreateFrom(string packageId, out PackageReference packageReference) =>
        TryCreateFromCore(packageId, null, out packageReference);

    public static bool TryCreateWithFallbackSourceFrom(
        string packageId,
        PackageSource fallbackSource,
        out PackageReference packageReference
    ) => TryCreateFromCore(packageId, fallbackSource, out packageReference);

    static bool TryCreateFromCore(
        string packageId,
        PackageSource? fallbackSource,
        out PackageReference packageReference
    )
    {
        var span = packageId.AsSpan();

        var everythingButSource = span.Split('/');
        everythingButSource.MoveNext();

        var left = span[everythingButSource.Current];
        string fullName = left[..].ToString();

        if (!everythingButSource.MoveNext())
        {
            if (fallbackSource is { })
            {
                packageReference = new(fullName, fallbackSource);
                return true;
            }

            throw new InvalidDataException("This constructor requires format 'author-name/source'");
        }

        var right = span[everythingButSource.Current.Start..];
        if (!PackageSourceId.Parse(right.ToString()).TryResolve(out var source))
        {
            packageReference = default;
            return false;
        }

        packageReference = new(fullName, source);
        return true;
    }

    public string ToStringSimpleWithSource() => $"{FullName}/{Source.Id}";

    public readonly Package Resolve() => (Package)this;

    public static explicit operator PackageReference(Package packageVersion) =>
        new(packageVersion.FullName, packageVersion.Source);

    public static implicit operator Package(PackageReference reference) =>
        reference.Source.nameToPackage[reference.FullName];
}

public readonly record struct PackageVersionReference
{
    public readonly string FullName { get; }
    public readonly PackageVersionNumber Version { get; }
    public readonly PackageSource Source { get; }

    public PackageVersionReference(
        string fullName,
        PackageVersionNumber version,
        PackageSource source
    )
    {
        FullName = fullName;
        Version = version;
        Source = source;
    }

    public static bool TryCreateFrom(
        string packageId,
        out PackageVersionReference versionReference
    ) => TryCreateFromCore(packageId, null, null, out versionReference);

    public static bool TryCreateFromWithVersion(
        string packageId,
        PackageVersionNumber version,
        out PackageVersionReference versionReference
    ) => TryCreateFromCore(packageId, null, version, out versionReference);

    public static bool TryCreateWithFallbackSourceFrom(
        string packageId,
        PackageSource fallbackSource,
        out PackageVersionReference versionReference
    ) => TryCreateFromCore(packageId, fallbackSource, null, out versionReference);

    static bool TryCreateFromCore(
        string packageId,
        PackageSource? fallbackSource,
        PackageVersionNumber? existingVersion,
        out PackageVersionReference versionReference
    )
    {
        var span = packageId.AsSpan();

        var everythingButSource = span.Split('/');
        everythingButSource.MoveNext();

        var left = span[everythingButSource.Current];

        string fullName;
        PackageVersionNumber version;

        if (existingVersion is { } existingVer)
        {
            fullName = left.ToString();
            version = existingVer;
        }
        else
        {
            var versionDivider = left.LastIndexOf('-');
            fullName = left[..versionDivider].ToString();
            version = new PackageVersionNumber(left[(versionDivider + 1)..]);
        }

        if (!everythingButSource.MoveNext())
        {
            if (fallbackSource is { })
            {
                versionReference = new(fullName, version, fallbackSource);
                return true;
            }

            throw new InvalidDataException(
                "This constructor requires format 'author-name-version/source'"
            );
        }

        var right = span[everythingButSource.Current.Start..];
        if (!PackageSourceId.Parse(right.ToString()).TryResolve(out var source))
        {
            versionReference = default;
            return false;
        }

        versionReference = new(fullName, version, source);
        return true;
    }

    public readonly PackageVersion Resolve() => (PackageVersion)this;

    public readonly PackageReference Package() => (PackageReference)this;

    public override string ToString() => $"{FullName}-{Version}/{Source.Id}";

    public static explicit operator PackageReference(PackageVersionReference reference) =>
        new(reference.FullName, reference.Source);

    public static explicit operator PackageVersionReference(PackageVersion packageVersion) =>
        new(packageVersion.GetFullName(), packageVersion.Version, packageVersion.Package.Source);

    public static implicit operator PackageVersion(PackageVersionReference reference)
    {
        if (
            !reference
                .Source.nameToPackage[reference.FullName]
                .TryGetVersion(reference.Version, out var packageVersion)
        )
        {
            throw new InvalidOperationException($"No versions found for: {reference}");
        }

        return packageVersion;
    }
}

public sealed partial record Package
{
    [JsonInclude]
    [JsonPropertyName("owner")]
    public Author Author { get; }

    [JsonInclude]
    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonIgnore]
    public string FullName { get; }

    [JsonInclude]
    [JsonPropertyName("versions")]
    public PackageVersion[] Versions { get; internal set; }

    [JsonIgnore]
    public PackageVersion Latest => Versions[0];

    [JsonIgnore]
    public PackageSource Source { get; internal set; } = null!;

    public Package(Author author, string name, PackageVersion[] versions)
    {
        Author = author;
        Name = name;
        FullName = $"{author.Name}-{name}";
        Versions = versions;
        foreach (var version in versions)
        {
            version.Package = this;
        }
    }

    public static bool TryGetPackage(
        PackageSource source,
        PackageReference packageReference,
        [NotNullWhen(true)] out Package? package
    ) => source.nameToPackage.TryGetValue(packageReference.FullName, out package);

    public static bool TryGetPackage(
        PackageSourceIndex index,
        PackageReference packageReference,
        [NotNullWhen(true)] out Package? package
    )
    {
        var refSource = packageReference.Source;
        if (TryGetPackage(refSource, packageReference, out package))
            return true;

        foreach (var userSource in index.Sources.Where(x => x.Visible && x.Source != refSource))
        {
            if (TryGetPackage(userSource.Source, packageReference, out package))
                return true;
        }

        return false;
    }

    public static bool TryGetPackageVersion(
        PackageSource source,
        PackageVersionReference versionReference,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = default;
        return TryGetPackage(source, (PackageReference)versionReference, out var package)
            && package.TryGetVersion(versionReference.Version, out packageVersion);
    }

    public static bool TryGetPackageVersion(
        PackageSourceIndex index,
        PackageVersionReference versionReference,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = default;
        return TryGetPackage(index, (PackageReference)versionReference, out var package)
            && package.TryGetVersion(versionReference.Version, out packageVersion);
    }

    public bool TryGetVersion(
        PackageVersionNumber version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => version == x.Version);
        if (packageVersion is not null)
        {
            return true;
        }

        packageVersion = Versions.FirstOrDefault();
        if (packageVersion is not null)
        {
            Cog.Debug(
                $"Version '{version}' not found -> using '{packageVersion.Version}' for '{ToStringSimpleWithSource()}'"
            );
            return true;
        }
        else
        {
            Cog.Error($"No versions of '{ToStringSimpleWithSource()}' exist");
            return false;
        }
    }

    public static Package? ResolvePackage(
        PackageSourceIndex index,
        string fullNameNoVersionWithSource
    )
    {
        if (!PackageReference.TryCreateFrom(fullNameNoVersionWithSource, out var packageReference))
        {
            Cog.Warning(
                $"Failed to create {nameof(PackageReference)} from '{fullNameNoVersionWithSource}'."
                    + " The source might not be supported."
            );
            return null;
        }

        if (!TryGetPackage(index, packageReference, out var package))
        {
            Cog.Debug($"Package '{packageReference}' is not found in any source.");
            return null;
        }

        return package;
    }

    public string ToStringSimpleWithSource()
    {
        var service = Source.Service;
        return $"{Author.Name}-{Name}/{service.Id}";
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Author.Name}-{Name} {{");

        foreach (var version in Versions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {version.Version}: {{");
            sb.AppendLine("    dependencies: [");
            foreach (var dependency in version.DependencyStrings)
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

public readonly record struct PackageVersionWithSource(
    PackageSourceId SourceId,
    PackageVersion PackageVersion
)
{
    public readonly PackageSource? GetSourceOrNull(PackageSourceIndex index)
    {
        if (!PackageSourceIndex.TryParseSourceId(SourceId, out var source))
        {
            Cog.Warning($"No package source found for '{SourceId}'" + new StackTrace(true));
            return null;
        }
        return source;
    }
}

public sealed partial record PackageVersion
{
    [JsonIgnore]
    public PackageVersionNumber Version { get; set; }

    [JsonInclude]
    [JsonPropertyName("version_number")]
    public string VersionString { get; }

    [JsonIgnore]
    public Package Package { get; internal set; } = null!;

    [JsonInclude]
    [JsonPropertyName("dependencies")]
    public string[] DependencyStrings { get; }

    [JsonInclude]
    [JsonPropertyName("namespace")]
    public Author Author => field is { Name: not null } ? field : Package.Author;

    [JsonInclude]
    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonInclude]
    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonIgnore]
    PackageVersion[]? _markedDependencies;

    public PackageVersion(
        Author author,
        string name,
        string? description,
        string versionString,
        string[] dependencyStrings
    )
    {
        Author = author;
        Name = name;
        Description = description ?? string.Empty;
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

    public PackageVersion[] MarkedDependencies(PackageSourceIndex index) =>
        _markedDependencies ??= [
            .. DependencyStrings.Select(ResolvePackageVersion(index)).Where(x => x is { })!,
        ];

    private Func<string, PackageVersion?> ResolvePackageVersion(PackageSourceIndex index) =>
        fullNameWithVersion =>
            ResolvePackageVersionWithFallbackSource(index, Package.Source, fullNameWithVersion);

    public static PackageVersion? ResolvePackageVersionWithFallbackSource(
        PackageSourceIndex index,
        PackageSource fallbackSource,
        string fullNameWithVersion
    )
    {
        if (
            !PackageVersionReference.TryCreateWithFallbackSourceFrom(
                fullNameWithVersion,
                fallbackSource,
                out var packageVersionReference
            )
        )
        {
            Cog.Warning(
                $"Failed to create {nameof(PackageVersionReference)} from '{fullNameWithVersion}'."
                    + " The source might not be supported."
            );
            return null;
        }

        if (!Package.TryGetPackageVersion(index, packageVersionReference, out var packageVersion))
        {
            Cog.Debug($"Package '{packageVersionReference}' has no versions found in any source.");
            return null;
        }

        return packageVersion;
    }

    public string GetFullName() => $"{Author}-{Name}";

    public PackageVersion WithVersion(PackageVersionNumber version)
    {
        return new(Author, Name, Description, version.ToString(), DependencyStrings);
    }

    public bool IsDownloaded() =>
        Package.Source.Service.IsPackageDownloaded((VisualPackageVersion)this);

    public Task<string?> ExtractAsync(CancellationToken cancellationToken = default) =>
        Package.Source.Service.ExtractAsync((VisualPackageVersion)this, cancellationToken);

    public Task<string> GetReadmeAsync(CancellationToken cancellationToken = default) =>
        Package.Source.Service.GetReadmeAsync((VisualPackageVersion)this, cancellationToken);

    public PackageVersion[] AllDependencies(PackageSourceIndex index)
    {
        HashSet<PackageVersion> actualDependencies = [];
        CollectDependencies(actualDependencies, index);
        return [.. actualDependencies];
    }

    void CollectDependencies(HashSet<PackageVersion> actualDependencies, PackageSourceIndex index)
    {
        foreach (var dependency in MarkedDependencies(index))
        {
            if (actualDependencies.Add(dependency))
            {
                dependency.CollectDependencies(actualDependencies, index);
            }
        }
    }

    public void CollectAllDependenciesToMap(
        Dictionary<PackageReference, PackageVersionReference> map,
        DependencyVersionResolution context,
        PackageSourceIndex index
    )
    {
        var dominant = index.GetOrMakeDominantPackage(this);

        switch (context)
        {
            case DependencyVersionResolution.Requested:
                if (map.AddOrUpdateToHigherVersion(dominant))
                {
                    CollectRequestedDependenciesToMapRecursive(map, index);
                }
                break;
            case DependencyVersionResolution.Latest:
                if (map.AddOrUpdateToHigherVersion(dominant))
                {
                    CollectLatestDependenciesToMapRecursive(map, index);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException($"Invalid enum value '{context}'");
        }
    }

    void CollectRequestedDependenciesToMapRecursive(
        Dictionary<PackageReference, PackageVersionReference> map,
        PackageSourceIndex index
    )
    {
        foreach (var dependency in MarkedDependencies(index))
        {
            var dominant = index.GetOrMakeDominantPackage(dependency);

            if (map.AddOrUpdateToHigherVersion(dominant))
            {
                dominant.CollectRequestedDependenciesToMapRecursive(map, index);
            }
        }
    }

    void CollectLatestDependenciesToMapRecursive(
        Dictionary<PackageReference, PackageVersionReference> map,
        PackageSourceIndex index
    )
    {
        foreach (var dependency in MarkedDependencies(index))
        {
            var dominant = index.GetOrMakeDominantPackage(dependency.Package);
            var latest = dominant.Latest;

            if (map.AddOrUpdateToHigherVersion(latest))
            {
                latest.CollectLatestDependenciesToMapRecursive(map, index);
            }
        }
    }

    public void CollectAllDependenciesToDestination(
        Dictionary<PackageReference, PackageVersionReference> map,
        Dictionary<PackageReference, PackageVersionReference> destination,
        PackageSourceIndex index
    )
    {
        var higher = map.GetHigherVersion(this);
        higher.CollectDependenciesToDestinationRecursive(map, destination, index);
    }

    void CollectDependenciesToDestinationRecursive(
        Dictionary<PackageReference, PackageVersionReference> map,
        Dictionary<PackageReference, PackageVersionReference> destination,
        PackageSourceIndex index
    )
    {
        foreach (var dependency in MarkedDependencies(index))
        {
            var dominant = index.GetOrMakeDominantPackage(dependency);

            var higher = map.GetHigherVersion(dominant);
            if (
                destination.TryAdd(
                    (PackageReference)higher.Package,
                    (PackageVersionReference)higher
                )
            )
            {
                higher.CollectDependenciesToDestinationRecursive(map, destination, index);
            }
        }
    }

    public override string ToString()
    {
        var service = Package.Source.Service;
        return $"{Package.Author.Name}-{Package.Name}-{Version}/{service.Id}";
    }

    public string ToStringWithoutVersion(string packageSourceId)
    {
        return $"{Author.Name}-{Name}/{packageSourceId}";
    }
}

[JsonConverter(typeof(AuthorConverter))]
public readonly partial record struct Author(string Name)
{
    public static implicit operator string(Author author) => author.Name;

    public static implicit operator Author(string author) => new(author);

    public override string ToString() => Name;
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

    /// <summary>
    /// SemVer build metadata; is ignored when determining version precedence.
    /// </summary>
    public string? Metadata { get; init; }

    public PackageVersionNumber(long major, long minor, long patch, string? metadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Metadata = metadata;
    }

    public PackageVersionNumber(scoped ReadOnlySpan<char> version)
    {
        var split = version.Split('.');
        split.MoveNext();
        Major = long.Parse(version[split.Current], CultureInfo.InvariantCulture);
        split.MoveNext();
        Minor = long.Parse(version[split.Current], CultureInfo.InvariantCulture);

        split.MoveNext();
        var lastDigitAndMetadata = version[split.Current.Start..];

        split = lastDigitAndMetadata.Split('+');
        split.MoveNext();
        Patch = long.Parse(lastDigitAndMetadata[split.Current], CultureInfo.InvariantCulture);

        if (lastDigitAndMetadata.Length == split.Current.End.Value)
            return;

        if (lastDigitAndMetadata[split.Current.End] is not '+')
        {
            throw new InvalidDataException($"Version '{version}' metadata doesn't start with '+'.");
        }

        split.MoveNext();
        Metadata = lastDigitAndMetadata[split.Current.Start..].ToString();
    }

    public readonly PackageVersionNumber WithMetadata(string metadata)
    {
        if (Metadata is null)
            return this with { Metadata = metadata };
        else
            return this with { Metadata = Metadata + '.' + metadata };
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

    public override string ToString()
    {
        if (Metadata is null)
            return $"{Major}.{Minor}.{Patch}";
        else
            return $"{Major}.{Minor}.{Patch}+{Metadata}";
    }

    public string ToStringWithWildcards() =>
        Patch is not long.MaxValue ? $"{Major}.{Minor}.{Patch}"
        : Minor is not long.MaxValue ? $"{Major}.{Minor}.*"
        : Major is not long.MaxValue ? $"{Major}.*"
        : "*";
}
