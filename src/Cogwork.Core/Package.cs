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

    public Package(Author author, string name, PackageVersionData[] versions)
    {
        Author = author;
        Name = name;
        Versions = [.. versions.Select(x => new PackageVersion(this, x))];
        _ = nameToPackage.TryAdd(ToStringSimple(), this);
    }

    public bool TryGetVersion(
        Version version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => x.Data.Version == version);
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {version.Data.Version}: {{");
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
            .. Data
                .DependencyStrings.Select(fullNameWithVersion =>
                {
                    var split = fullNameWithVersion.Split('-');
                    var fullName = string.Join('-', split.Take(2));
                    Version version;
                    try
                    {
                        version = new(split[^1]);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(fullNameWithVersion, ex);
                    }

                    if (!Package.nameToPackage.TryGetValue(fullName, out var packageFromName))
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

    public void CollectAllDependencies(Dictionary<Package, PackageVersion> map)
    {
        if (map.AddOrUpdateToHigherVersion(this))
        {
            CollectDependencies(map);
        }
    }

    void CollectDependencies(Dictionary<Package, PackageVersion> map)
    {
        foreach (var dependency in MarkedDependencies)
        {
            if (map.AddOrUpdateToHigherVersion(dependency))
            {
                dependency.CollectDependencies(map);
            }
        }
    }

    public void CollectAllDependencies(
        Dictionary<Package, PackageVersion> map,
        Dictionary<Package, PackageVersion> destination
    )
    {
        var higher = map.GetHigherVersion(this);
        higher.CollectDependencies(map, destination);
    }

    void CollectDependencies(
        Dictionary<Package, PackageVersion> map,
        Dictionary<Package, PackageVersion> destination
    )
    {
        foreach (var dependency in MarkedDependencies)
        {
            var higher = map.GetHigherVersion(dependency);
            if (destination.TryAdd(higher.Package, higher))
            {
                higher.CollectDependencies(map, destination);
            }
        }
    }

    public PackageVersionData Data { get; }
    public Package Package { get; }

    public PackageVersion(Package package, PackageVersionData versionData)
    {
        Package = package;
        Data = versionData;
    }

    public override string ToString()
    {
        return $"{Package.Author.Name}-{Package.Name}-{Data.Version}";
    }
}

public readonly record struct PackageVersionData(Version Version, string[] DependencyStrings) { }

public readonly record struct Author(string Name)
{
    public static implicit operator string(Author author) => author.Name;

    public static implicit operator Author(string author) => new(author);
}
