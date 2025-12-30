using Cogwork.Core;

namespace Cogwork.Tests.Management;

public class ModListTests
{
    [Fact]
    public void PrintModList()
    {
        foreach (var package in Repository.GetAllPackages())
        {
            Console.WriteLine(package);
        }

        var allPackages = Repository.GetAllPackages();

        var peaklibItems = allPackages.First(x => x is { Name: "PEAKLib.Items" });
        var peaklibCore = allPackages.First(x => x is { Name: "PEAKLib.Core" });

        var modList = new ModList();

        modList.Add(peaklibItems.Versions[^1]);
        Console.WriteLine(modList);

        modList.Add(peaklibCore);
        Console.WriteLine(modList);
    }
}
