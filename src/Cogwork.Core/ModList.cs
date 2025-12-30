using System.Runtime.InteropServices;
using System.Text;

namespace Cogwork.Core;

public class ModList
{
    public Dictionary<Package, PackageVersion> Added { get; } = [];
    public Dictionary<Package, PackageVersion> Dependencies { get; private set; } = [];

    public void Add(Package package) => Add(package.Versions.First());

    public void Add(PackageVersion package)
    {
        Added.AddOrResolveHigherVersion(package);
        RebuildDependencies();
    }

    public void Remove(Package package)
    {
        Added.Remove(package);
        RebuildDependencies();
    }

    public void RebuildDependencies()
    {
        Dictionary<Package, PackageVersion> allDependencies = [];

        foreach (var added in Added)
        {
            foreach (var dependency in added.Value.AllDependencies)
            {
                allDependencies.AddOrResolveHigherVersion(dependency);
            }
        }

        foreach (var added in Added)
        {
            allDependencies.Remove(added.Key);
        }

        Dependencies = allDependencies;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Added:");
        foreach (var added in Added.Values)
        {
            sb.Append("  ");
            sb.AppendLine(added.ToString());
        }

        sb.AppendLine("Dependencies:");
        foreach (var dependency in Dependencies.Values.Reverse())
        {
            sb.Append("  ");
            sb.AppendLine(dependency.ToString());
        }

        return sb.ToString();
    }
}

public static class ModListExtensions
{
    public static void AddOrResolveHigherVersion(
        this Dictionary<Package, PackageVersion> dictionary,
        PackageVersion package
    )
    {
        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary,
            package.Package,
            out var exists
        );

        if (!exists)
        {
            value = package;
            return;
        }

        if (value!.Data.Version > package.Data.Version)
        {
            return;
        }
        else
        {
            value = package;
        }
    }
}
