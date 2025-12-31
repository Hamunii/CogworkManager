using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Cogwork.Core;

public record Package
{
    internal static ConcurrentDictionary<string, Package> nameToPackage = [];
    public Author Author { get; }
    public string Name { get; }
    public PackageVersion[] Versions { get; }
    public PackageVersion Latest => Versions[0];

    public Package(Author author, string name, Func<Package, PackageVersion[]> versions)
    {
        Author = author;
        Name = name;
        Versions = versions(this);

        _ = nameToPackage
            .GetAlternateLookup<ReadOnlySpan<char>>()
            .TryAdd($"{Author.Name}-{Name}", this);
    }

    public Package(ReadOnlySpan<char> fullName, Func<Package, PackageVersion[]> versions)
    {
        var enumerator = fullName.Split('-');
        enumerator.MoveNext();
        Author = fullName[enumerator.Current].ToString();
        enumerator.MoveNext();
        Name = fullName[enumerator.Current].ToString();
        Versions = versions(this);

        _ = nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>().TryAdd(fullName, this);
    }

    public bool TryGetVersion(
        Version version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => x.Version == version);
        if (packageVersion is null)
        {
            Console.Error.WriteLine($"Version '{version}' not found for '{ToStringSimple()}'");
            packageVersion = Versions.FirstOrDefault();
            if (packageVersion is null)
            {
                Console.Error.WriteLine($"No versions of '{ToStringSimple()}' exist");
            }
        }
        return packageVersion is { };
    }

    public string ToStringSimple() => $"{Author.Name}-{Name}";

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

public record PackageVersion
{
    public PackageVersion[] MarkedDependencies =>
        field ??= [
            .. _dependencyStrings
                .Select(fullNameWithVersionString =>
                {
                    var fullNameWithVersion = fullNameWithVersionString.AsSpan();
                    var split = fullNameWithVersion.Split('-');
                    split.MoveNext();
                    split.MoveNext();
                    var fullName = fullNameWithVersion[0..split.Current.End];
                    split.MoveNext();
                    Version version;
                    try
                    {
                        version = new(fullNameWithVersion[split.Current].ToString());
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(fullNameWithVersionString, ex);
                    }

                    if (
                        !Package
                            .nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>()
                            .TryGetValue(fullName, out var packageFromName)
                    )
                    {
                        Console.Error.WriteLine($"Package for '{fullName}' was not found.");
                        return null;
                    }

                    if (!packageFromName.TryGetVersion(version, out var packageVersion))
                    {
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

    public Version Version { get; }
    public Package Package { get; }

    readonly string[] _dependencyStrings;

    public PackageVersion(Package package, Version version, string[] dependencyStrings)
    {
        Package = package;
        Version = version;
        _dependencyStrings = dependencyStrings;
    }

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
        return $"{Package.Author.Name}-{Package.Name}-{Version}";
    }
}

public readonly record struct Author(string Name)
{
    public static implicit operator string(Author author) => author.Name;

    public static implicit operator Author(string author) => new(author);
}
