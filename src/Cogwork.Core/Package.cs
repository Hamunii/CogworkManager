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
        _ = nameToPackage.TryAdd($"{author.Name}-{name}", this);
    }

    public bool TryGetVersion(
        Version version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => x.Data.Version == version);
        return packageVersion is { };
    }

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
            .. Data.DependencyStrings.Select(fullNameWithVersion =>
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
                    throw new ArgumentException(
                        $$"""
                        Package for '{Author}-{Name}' ({{Package.Author.Name}}-{{Package.Name}}) was not found.
                        """
                    );
                }

                if (!packageFromName.TryGetVersion(version, out var packageVersion))
                {
                    throw new ArgumentException(
                        $$"""
                        Version '{{version}}' of '{{Package.Author.Name}}-{{Package.Name}}' was not found.
                        """
                    );
                }

                return packageVersion;
            }),
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
            dependency.CollectDependencies(actualDependencies);
            actualDependencies.Add(dependency);
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
