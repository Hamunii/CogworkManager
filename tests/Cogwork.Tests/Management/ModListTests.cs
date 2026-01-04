namespace Cogwork.Tests.Management;

public class ModListTests
{
    [Fact]
    public void PrintModList()
    {
        foreach (var package in MockData.GetAllPackages())
        {
            Console.WriteLine(package);
        }

        PackageRepo.Silksong.GetAllPackages();

        var allPackages = MockData.GetAllPackages();

        var peaklibItems = allPackages.First(x => x is { Name: "PEAKLib.Items" });
        var peaklibCore = allPackages.First(x => x is { Name: "PEAKLib.Core" });

        var modList = PackageRepo.Silksong.GetModList("test");

        modList.Add(peaklibItems.Versions[^1]);
        Console.WriteLine(modList);

        modList.Add(peaklibCore);
        Console.WriteLine(modList);

        modList.Add(peaklibItems);
        Console.WriteLine(modList);
    }
}
