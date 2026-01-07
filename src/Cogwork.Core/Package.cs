using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Cogwork.Core;

public record Package
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
    public GamePackageRepo PackageRepo { get; internal set; } = null!;

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
        GamePackageRepoList repoList,
        ReadOnlySpan<char> fullName,
        [NotNullWhen(true)] out Package? package,
        bool hasVersion,
        out Version? version,
        [NotNullWhen(true)] out GamePackageRepo repo
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

        if (!split.MoveNext())
        {
            repo = repoList.Default;
        }
        else
        {
            var repository = fullName[split.Current];
            if (repository == "ts")
            {
                repo = repoList.Default;
            }
            else
            {
                throw new NotImplementedException(
                    $"Only 'ts' (as Thunderstore) is supported. Was '{repository}'."
                );
            }
        }

        return repo
            .nameToPackage.GetAlternateLookup<ReadOnlySpan<char>>()
            .TryGetValue(name, out package);
    }

    public static PackageVersion GetPackageVersion(
        GamePackageRepoList repoList,
        ReadOnlySpan<char> fullNameWithVersion
    )
    {
        if (
            !TryGetPackage(
                repoList,
                fullNameWithVersion,
                out var package,
                hasVersion: true,
                out var version,
                out _
            )
        )
        {
            throw new ArgumentException($"Package for '{fullNameWithVersion}' doesn't exist.");
        }

        if (!package.TryGetVersion(version!, out var packageVersion))
        {
            throw new ArgumentException($"Version for '{fullNameWithVersion}' doesn't exist.");
        }
        return packageVersion;
    }

    public bool TryGetVersion(
        Version version,
        [NotNullWhen(true)] out PackageVersion? packageVersion
    )
    {
        packageVersion = Versions.FirstOrDefault(x => x.Version == version);
        if (packageVersion is null)
        {
            Cog.Error($"Version '{version}' not found for '{ToStringSimple()}'");
            packageVersion = Versions.FirstOrDefault();
            if (packageVersion is null)
            {
                Cog.Error($"No versions of '{ToStringSimple()}' exist");
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
            .. DependencyStrings
                .Select(fullNameWithVersion =>
                {
                    if (
                        !Package.TryGetPackage(
                            Package.PackageRepo.RepoList,
                            fullNameWithVersion,
                            out var package,
                            hasVersion: true,
                            out var version,
                            out var repo
                        )
                    )
                    {
                        Cog.Error(
                            $"Package for '{fullNameWithVersion}' in '{repo}' was not found."
                        );
                        return null;
                    }

                    if (!package.TryGetVersion(version!, out var packageVersion))
                    {
                        Cog.Error($"Package '{fullNameWithVersion}' has no versions in '{repo}'.");
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

    [JsonInclude]
    [JsonPropertyName("version_number")]
    public Version Version { get; }
    public Package Package { get; internal set; } = null!;

    [JsonInclude]
    [JsonPropertyName("dependencies")]
    string[] DependencyStrings { get; }

    public PackageVersion(Version version, string[] dependencyStrings)
    {
        Version = version;
        DependencyStrings = dependencyStrings;
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
        var repoHandler = Package.PackageRepo.RepoHander;
        var at = repoHandler is RepoThunderstoreHandler ? "ts" : repoHandler.Url.Host;

        return $"{Package.Author.Name}-{Package.Name}-{Version}-{at}";
    }
}

public readonly record struct Author(string Name)
{
    public static implicit operator string(Author author) => author.Name;

    public static implicit operator Author(string author) => new(author);
}
